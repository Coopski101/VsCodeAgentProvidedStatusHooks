using System.Text.Json;
using BeaconCore.Events;

namespace BeaconCore.Hooks;

public sealed class CopilotTranscriptWatcher : BackgroundService
{
    private readonly EventBus _bus;
    private readonly ILogger<CopilotTranscriptWatcher> _logger;
    private readonly Lock _lock = new();

    private string? _transcriptPath;
    private long _lastFilePosition;
    private readonly HashSet<string> _pendingToolCallIds = [];
    private CancellationTokenSource? _waitingTimerCts;
    private bool _waitingPublished;

    private static readonly TimeSpan ApprovalDetectionDelay = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(300);

    public CopilotTranscriptWatcher(EventBus bus, ILogger<CopilotTranscriptWatcher> logger)
    {
        _bus = bus;
        _logger = logger;
    }

    public void SetTranscriptPath(string path)
    {
        lock (_lock)
        {
            if (string.Equals(_transcriptPath, path, StringComparison.OrdinalIgnoreCase))
                return;

            _transcriptPath = path;
            _lastFilePosition = 0;
            _pendingToolCallIds.Clear();
            _waitingPublished = false;

            _logger.LogInformation("Transcript watcher targeting: {Path}", path);

            SeekToEnd(path);
        }
    }

    private void SeekToEnd(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                using var fs = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite
                );
                _lastFilePosition = fs.Length;
                _logger.LogDebug("Seeked to position {Pos} in transcript", _lastFilePosition);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to seek transcript file");
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Copilot transcript watcher started");

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(PollInterval, stoppingToken);

            string? path;
            lock (_lock)
            {
                path = _transcriptPath;
            }

            if (path is null)
                continue;

            try
            {
                ReadNewLines(path);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error reading transcript");
            }
        }
    }

    private void ReadNewLines(string path)
    {
        if (!File.Exists(path))
            return;

        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

        if (fs.Length <= _lastFilePosition)
            return;

        fs.Seek(_lastFilePosition, SeekOrigin.Begin);

        using var reader = new StreamReader(fs);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            ProcessLine(line);
        }

        _lastFilePosition = fs.Position;
    }

    private void ProcessLine(string line)
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
                    HandleAssistantMessage(root);
                    break;
                case "tool.execution_start":
                    HandleToolExecutionStart(root);
                    break;
            }
        }
        catch (JsonException) { }
    }

    private void HandleAssistantMessage(JsonElement root)
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
                _pendingToolCallIds.Add(id);
            }
        }

        _logger.LogDebug(
            "Transcript: {Count} tool request(s) pending approval: {Tools}",
            toolCallIds.Count,
            string.Join(", ", toolCallIds.Select(id => id[^8..]))
        );

        StartApprovalTimer();
    }

    private void HandleToolExecutionStart(JsonElement root)
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
            wasWaiting = _waitingPublished;
            _pendingToolCallIds.Remove(toolCallId);
            allCleared = _pendingToolCallIds.Count == 0;

            if (allCleared)
            {
                _waitingTimerCts?.Cancel();
                _waitingTimerCts?.Dispose();
                _waitingTimerCts = null;
            }
        }

        if (wasWaiting && allCleared)
        {
            _logger.LogInformation("Transcript: tool approved, publishing Clear");

            lock (_lock)
            {
                _waitingPublished = false;
            }

            _bus.Publish(
                new BeaconEvent
                {
                    EventType = BeaconEventType.Clear,
                    Source = AgentSource.Copilot,
                    HookEvent = "TranscriptApproval",
                    Reason = "User approved tool execution",
                }
            );
        }
        else
        {
            _logger.LogDebug("Transcript: tool {Id} started (auto-approved)", toolCallId[^8..]);
        }
    }

    private void StartApprovalTimer()
    {
        lock (_lock)
        {
            _waitingTimerCts?.Cancel();
            _waitingTimerCts?.Dispose();
            _waitingTimerCts = new CancellationTokenSource();
        }

        var cts = _waitingTimerCts;

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
                hasPending = _pendingToolCallIds.Count > 0;
            }

            if (!hasPending)
                return;

            lock (_lock)
            {
                _waitingPublished = true;
            }

            _logger.LogInformation(
                "Transcript: no tool.execution_start after {Delay}ms — publishing Waiting (Copilot approval pending)",
                ApprovalDetectionDelay.TotalMilliseconds
            );

            _bus.Publish(
                new BeaconEvent
                {
                    EventType = BeaconEventType.Waiting,
                    Source = AgentSource.Copilot,
                    HookEvent = "TranscriptApprovalPending",
                    Reason = "Copilot waiting for user to approve tool execution",
                }
            );
        });
    }

    public override void Dispose()
    {
        _waitingTimerCts?.Cancel();
        _waitingTimerCts?.Dispose();
        base.Dispose();
    }
}
