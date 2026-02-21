namespace BeaconCore.Platform;

public sealed class NullPlatformMonitor : IPlatformMonitor
{
#pragma warning disable CS0067
    public event Action<string>? AppFocusGained;
#pragma warning restore CS0067

    public TimeSpan UserIdleDuration => TimeSpan.Zero;

    public bool IsAppFocused(string processName) => false;

    public Task StartAsync(CancellationToken ct) => Task.CompletedTask;

    public void Dispose() { }
}
