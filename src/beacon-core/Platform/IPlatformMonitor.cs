namespace BeaconCore.Platform;

public interface IPlatformMonitor : IDisposable
{
    event Action<string>? AppFocusGained;
    TimeSpan UserIdleDuration { get; }
    bool IsAppFocused(string processName);
    Task StartAsync(CancellationToken ct);
}
