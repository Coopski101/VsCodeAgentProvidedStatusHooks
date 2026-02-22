using System.Text.Json;
using BeaconCore.Config;
using BeaconCore.Events;
using BeaconCore.Sessions;

namespace BeaconCore.Hooks;

public sealed class CopilotTranscriptWatcher : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly BeaconConfig _config;
    private readonly ILogger<CopilotTranscriptWatcher> _logger;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, TranscriptSession> _sessions = new();

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(300);

    public CopilotTranscriptWatcher(
        IServiceProvider services,
        BeaconConfig config,
        ILogger<CopilotTranscriptWatcher> logger
    )
    {
        _services = services;
        _config = config;
        _logger = logger;
    }

    public void SetTranscriptPath(string sessionId, string path)
    {
        lock (_lock)
        {
            if (
                _sessions.TryGetValue(sessionId, out var existing)
                && string.Equals(existing.Path, path, StringComparison.OrdinalIgnoreCase)
            )
                return;

            var ts = new TranscriptSession { SessionId = sessionId, Path = path };

            _logger.LogInformation(
                "Transcript watcher targeting session {Session}: {Path}",
                sessionId,
                path
            );

            SeekToEnd(ts);
            _sessions[sessionId] = ts;
        }
    }

    private static void SeekToEnd(TranscriptSession ts)
    {
        try
        {
            if (File.Exists(ts.Path))
            {
                using var fs = new FileStream(
                    ts.Path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite
                );
                ts.LastFilePosition = fs.Length;
            }
        }
        catch { }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Copilot transcript watcher started");

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(PollInterval, stoppingToken);

            List<TranscriptSession> snapshot;
            lock (_lock)
            {
                snapshot = [.. _sessions.Values];
            }

            foreach (var ts in snapshot)
            {
                try
                {
                    ReadNewLines(ts);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(
                        ex,
                        "Error reading transcript for session {Session}",
                        ts.SessionId
                    );
                }
            }
        }
    }

    private void ReadNewLines(TranscriptSession ts)
    {
        if (!File.Exists(ts.Path))
            return;

        using var fs = new FileStream(ts.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

        if (fs.Length <= ts.LastFilePosition)
            return;

        fs.Seek(ts.LastFilePosition, SeekOrigin.Begin);

        using var reader = new StreamReader(fs);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            ProcessLine(ts, line);
        }

        ts.LastFilePosition = fs.Position;
    }

    private void ProcessLine(TranscriptSession ts, string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;

            if (!root.TryGetProperty("type", out var typeProp))
                return;

            var type = typeProp.GetString();

            switch (type)
            {
                case "assistant.message":
                    HandleAssistantMessage(ts, root);
                    break;
                case "tool.execution_start":
                    HandleToolExecutionStart(ts, root);
                    break;
            }
        }
        catch (JsonException) { }
    }

    private void HandleAssistantMessage(TranscriptSession ts, JsonElement root)
    {
        if (!root.TryGetProperty("data", out var data))
            return;

        if (!data.TryGetProperty("toolRequests", out var toolRequests))
            return;

        if (toolRequests.ValueKind != JsonValueKind.Array || toolRequests.GetArrayLength() == 0)
            return;

        var toolCallIds = new List<string>();
        foreach (var req in toolRequests.EnumerateArray())
        {
            if (req.TryGetProperty("toolCallId", out var idProp))
            {
                var id = idProp.GetString();
                if (id is not null)
                    toolCallIds.Add(id);
            }
        }

        if (toolCallIds.Count == 0)
            return;

        lock (_lock)
        {
            foreach (var id in toolCallIds)
            {
                ts.PendingToolCallIds.Add(id);
            }
        }

        _logger.LogDebug(
            "Transcript [{Session}]: {Count} tool request(s) pending approval",
            ts.SessionId,
            toolCallIds.Count
        );

        StartApprovalTimer(ts);
    }

    private void HandleToolExecutionStart(TranscriptSession ts, JsonElement root)
    {
        if (!root.TryGetProperty("data", out var data))
            return;

        if (!data.TryGetProperty("toolCallId", out var idProp))
            return;

        var toolCallId = idProp.GetString();
        if (toolCallId is null)
            return;

        bool wasWaiting;
        bool allCleared;

        lock (_lock)
        {
            wasWaiting = ts.WaitingPublished;
            ts.PendingToolCallIds.Remove(toolCallId);
            allCleared = ts.PendingToolCallIds.Count == 0;

            if (allCleared)
            {
                ts.WaitingTimerCts?.Cancel();
                ts.WaitingTimerCts?.Dispose();
                ts.WaitingTimerCts = null;
            }
        }

        if (wasWaiting && allCleared)
        {
            _logger.LogInformation(
                "Transcript [{Session}]: tool approved, sending Clear",
                ts.SessionId
            );

            lock (_lock)
            {
                ts.WaitingPublished = false;
            }

            var orchestrator = _services.GetRequiredService<SessionOrchestrator>();
            orchestrator.HandleStateChange(
                ts.SessionId,
                AgentSource.Copilot,
                HookAction.Clear,
                "TranscriptApproval",
                "User approved tool execution"
            );
        }
        else
        {
            _logger.LogDebug(
                "Transcript [{Session}]: tool {Id} started (auto-approved)",
                ts.SessionId,
                toolCallId[^8..]
            );
        }
    }

    private void StartApprovalTimer(TranscriptSession ts)
    {
        lock (_lock)
        {
            ts.WaitingTimerCts?.Cancel();
            ts.WaitingTimerCts?.Dispose();
            ts.WaitingTimerCts = new CancellationTokenSource();
        }

        var cts = ts.WaitingTimerCts;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(_config.AutoApprovedToolDetectionDelayMs, cts!.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            bool hasPending;
            lock (_lock)
            {
                hasPending = ts.PendingToolCallIds.Count > 0;
            }

            if (!hasPending)
                return;

            lock (_lock)
            {
                ts.WaitingPublished = true;
            }

            _logger.LogInformation(
                "Transcript [{Session}]: no tool.execution_start after {Delay}ms — sending Waiting",
                ts.SessionId,
                _config.AutoApprovedToolDetectionDelayMs
            );

            var orchestrator = _services.GetRequiredService<SessionOrchestrator>();
            orchestrator.HandleStateChange(
                ts.SessionId,
                AgentSource.Copilot,
                HookAction.Waiting,
                "TranscriptApprovalPending",
                "Copilot waiting for user to approve tool execution"
            );
        });
    }

    public override void Dispose()
    {
        lock (_lock)
        {
            foreach (var ts in _sessions.Values)
            {
                ts.WaitingTimerCts?.Cancel();
                ts.WaitingTimerCts?.Dispose();
            }
        }
        base.Dispose();
    }

    private sealed class TranscriptSession
    {
        public required string SessionId { get; init; }
        public required string Path { get; init; }
        public long LastFilePosition { get; set; }
        public HashSet<string> PendingToolCallIds { get; } = [];
        public CancellationTokenSource? WaitingTimerCts { get; set; }
        public bool WaitingPublished { get; set; }
    }
}
