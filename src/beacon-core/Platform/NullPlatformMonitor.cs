namespace BeaconCore.Platform;

public sealed class NullPlatformMonitor : IPlatformMonitor
{
    public nint? FocusedWindowHandle => null;
    public string? FocusedWindowProcessName => null;

#pragma warning disable CS0067
    public event Action<nint, string>? WindowFocusChanged;
#pragma warning restore CS0067

    public TimeSpan UserIdleDuration => TimeSpan.Zero;

    public uint LastInputTick => 0;

    public bool IsWindowAlive(nint hwnd) => false;

    public Task StartAsync(CancellationToken ct) => Task.CompletedTask;

    public void Dispose() { }
}
