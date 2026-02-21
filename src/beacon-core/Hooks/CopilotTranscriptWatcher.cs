using System.Text.Json;
using BeaconCore.Events;

namespace BeaconCore.Hooks;

public sealed class CopilotTranscriptWatcher : BackgroundService
{
    private readonly EventBus _bus;
    private readonly ILogger<CopilotTranscriptWatcher> _logger;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, TranscriptSession> _sessions = new(
        StringComparer.OrdinalIgnoreCase
    );

    private static readonly TimeSpan ApprovalDetectionDelay = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(300);

    public CopilotTranscriptWatcher(EventBus bus, ILogger<CopilotTranscriptWatcher> logger)
    {
        _bus = bus;
        _logger = logger;
    }

    public void RegisterTranscript(string sessionId, string path)
    {
        lock (_lock)
        {
            if (
                _sessions.TryGetValue(sessionId, out var existing)
                && string.Equals(existing.Path, path, StringComparison.OrdinalIgnoreCase)
            )
                return;

            var session = new TranscriptSession { SessionId = sessionId, Path = path };
            _sessions[sessionId] = session;

            _logger.LogInformation(
                "Transcript watcher registered session {SessionId} -> {Path}",
                sessionId,
                path
            );

            SeekToEnd(session);
        }
    }

    private static void SeekToEnd(TranscriptSession session)
    {
        if (!File.Exists(session.Path))
            return;

        using var fs = new FileStream(
            session.Path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite
        );
        session.LastFilePosition = fs.Length;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Copilot transcript watcher started (multi-session)");

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(PollInterval, stoppingToken);

            List<TranscriptSession> snapshot;
            lock (_lock)
            {
                snapshot = [.. _sessions.Values];
            }

            foreach (var session in snapshot)
            {
                try
                {
                    ReadNewLines(session);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(
                        ex,
                        "Error reading transcript for session {SessionId}",
                        session.SessionId
                    );
                }
            }
        }
    }

    private void ReadNewLines(TranscriptSession session)
    {
        if (!File.Exists(session.Path))
            return;

        using var fs = new FileStream(
            session.Path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite
        );

        if (fs.Length <= session.LastFilePosition)
            return;

        fs.Seek(session.LastFilePosition, SeekOrigin.Begin);

        using var reader = new StreamReader(fs);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            ProcessLine(session, line);
        }

        session.LastFilePosition = fs.Position;
    }

    private void ProcessLine(TranscriptSession session, string line)
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
                    HandleAssistantMessage(session, root);
                    break;
                case "tool.execution_start":
                    HandleToolExecutionStart(session, root);
                    break;
            }
        }
        catch (JsonException) { }
    }

    private void HandleAssistantMessage(TranscriptSession session, JsonElement root)
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
                session.PendingToolCallIds.Add(id);
        }

        _logger.LogDebug(
            "Session {SessionId}: {Count} tool request(s) pending approval",
            session.SessionId[..Math.Min(8, session.SessionId.Length)],
            toolCallIds.Count
        );

        StartApprovalTimer(session);
    }

    private void HandleToolExecutionStart(TranscriptSession session, JsonElement root)
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
            wasWaiting = session.WaitingPublished;
            session.PendingToolCallIds.Remove(toolCallId);
            allCleared = session.PendingToolCallIds.Count == 0;

            if (allCleared)
            {
                session.WaitingTimerCts?.Cancel();
                session.WaitingTimerCts?.Dispose();
                session.WaitingTimerCts = null;
            }
        }

        if (wasWaiting && allCleared)
        {
            _logger.LogInformation(
                "Session {SessionId}: tool approved, publishing Clear",
                session.SessionId[..Math.Min(8, session.SessionId.Length)]
            );

            lock (_lock)
            {
                session.WaitingPublished = false;
            }

            _bus.Publish(
                new BeaconEvent
                {
                    EventType = BeaconEventType.Clear,
                    Source = AgentSource.Copilot,
                    SessionId = session.SessionId,
                    HookEvent = "TranscriptApproval",
                    Reason = "User approved tool execution",
                }
            );
        }
    }

    private void StartApprovalTimer(TranscriptSession session)
    {
        lock (_lock)
        {
            session.WaitingTimerCts?.Cancel();
            session.WaitingTimerCts?.Dispose();
            session.WaitingTimerCts = new CancellationTokenSource();
        }

        var cts = session.WaitingTimerCts;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(ApprovalDetectionDelay, cts!.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            bool hasPending;
            lock (_lock)
            {
                hasPending = session.PendingToolCallIds.Count > 0;
            }

            if (!hasPending)
                return;

            lock (_lock)
            {
                session.WaitingPublished = true;
            }

            _logger.LogInformation(
                "Session {SessionId}: no tool.execution_start after {Delay}ms — publishing Waiting",
                session.SessionId[..Math.Min(8, session.SessionId.Length)],
                ApprovalDetectionDelay.TotalMilliseconds
            );

            _bus.Publish(
                new BeaconEvent
                {
                    EventType = BeaconEventType.Waiting,
                    Source = AgentSource.Copilot,
                    SessionId = session.SessionId,
                    HookEvent = "TranscriptApprovalPending",
                    Reason = "Copilot waiting for user to approve tool execution",
                }
            );
        });
    }

    public override void Dispose()
    {
        lock (_lock)
        {
            foreach (var session in _sessions.Values)
            {
                session.WaitingTimerCts?.Cancel();
                session.WaitingTimerCts?.Dispose();
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
