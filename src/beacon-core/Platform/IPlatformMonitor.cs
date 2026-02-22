namespace BeaconCore.Platform;

public interface IPlatformMonitor : IDisposable
{
    nint? FocusedWindowHandle { get; }
    string? FocusedWindowProcessName { get; }
    event Action<nint, string>? WindowFocusChanged;
    TimeSpan UserIdleDuration { get; }
    uint LastInputTick { get; }
    bool IsWindowAlive(nint hwnd);
    Task StartAsync(CancellationToken ct);
}
