using BeaconCore.Events;

namespace BeaconCore.Sessions;

public sealed class SessionInfo
{
    public string SessionId { get; }
    public nint WindowHandle { get; }
    public AgentSource Source { get; set; }
    public BeaconMode InternalState { get; set; } = BeaconMode.Idle;
    public BeaconMode PublishedState { get; set; } = BeaconMode.Idle;
    public DateTimeOffset StateChangedAt { get; set; } = DateTimeOffset.UtcNow;
    public uint InputTickAtStateChange { get; set; }
    public CancellationTokenSource? AfkTimerCts { get; set; }

    public SessionInfo(string sessionId, nint windowHandle, AgentSource source)
    {
        SessionId = sessionId;
        WindowHandle = windowHandle;
        Source = source;
    }

    public void CancelAfkTimer()
    {
        AfkTimerCts?.Cancel();
        AfkTimerCts?.Dispose();
        AfkTimerCts = null;
    }
}
