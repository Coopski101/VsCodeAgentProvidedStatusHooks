using System.Diagnostics;
using BeaconCore.Config;
using BeaconCore.Events;

namespace BeaconCore.Platform;

public sealed class PresenceClearService : BackgroundService
{
    private readonly IPlatformMonitor _monitor;
    private readonly EventBus _bus;
    private readonly BeaconConfig _config;
    private readonly ILogger<PresenceClearService> _logger;

    private bool _wasAfk;
    private int _lastVscodeProcessCount;

    public PresenceClearService(
        IPlatformMonitor monitor,
        EventBus bus,
        BeaconConfig config,
        ILogger<PresenceClearService> logger
    )
    {
        _monitor = monitor;
        _bus = bus;
        _config = config;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await _monitor.StartAsync(stoppingToken);

        _monitor.AppFocusGained += OnAppFocusGained;

        _logger.LogInformation(
            "Presence clear service started (AFK threshold={Threshold}s, poll={Poll}ms)",
            _config.AfkThresholdSeconds,
            _config.PollIntervalMs
        );

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(_config.PollIntervalMs, stoppingToken);

            CheckVscodeProcesses();

            var idle = _monitor.UserIdleDuration;
            var isAfk = idle > TimeSpan.FromSeconds(_config.AfkThresholdSeconds);

            if (isAfk && !_wasAfk)
            {
                _logger.LogDebug("User went AFK (idle {Idle})", idle);
            }
            else if (_wasAfk && !isAfk)
            {
                _logger.LogDebug("User returned from AFK (idle {Idle})", idle);

                if (_monitor.IsAppFocused(_config.VscodeProcessName))
                {
                    _logger.LogInformation(
                        "AFK return with VS Code focused — publishing Clear (mode={Mode})",
                        _bus.CurrentMode
                    );
                    _bus.Publish(
                        new BeaconEvent
                        {
                            EventType = BeaconEventType.Clear,
                            Source = AgentSource.Unknown,
                            SessionId = BeaconEvent.BroadcastSessionId,
                            HookEvent = "AfkReturn",
                            Reason = "User returned from AFK while VS Code is focused",
                        }
                    );
                }
                else
                {
                    _logger.LogDebug("AFK return but VS Code not focused — skipping Clear");
                }
            }

            _wasAfk = isAfk;
        }
    }

    private void CheckVscodeProcesses()
    {
        try
        {
            var count = Process.GetProcessesByName(_config.VscodeProcessName).Length;

            if (_lastVscodeProcessCount > 0 && count < _lastVscodeProcessCount)
            {
                _logger.LogInformation(
                    "VS Code process count dropped {Old} -> {New} — publishing Clear",
                    _lastVscodeProcessCount,
                    count
                );
                _bus.Publish(
                    new BeaconEvent
                    {
                        EventType = BeaconEventType.Clear,
                        Source = AgentSource.Unknown,
                        SessionId = BeaconEvent.BroadcastSessionId,
                        HookEvent = "VsCodeClosed",
                        Reason = "A VS Code window was closed",
                    }
                );
            }

            _lastVscodeProcessCount = count;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error checking VS Code processes");
        }
    }

    private void OnAppFocusGained(string processName)
    {
        if (
            string.Equals(
                processName,
                _config.VscodeProcessName,
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            _logger.LogInformation(
                "VS Code gained focus — publishing Clear (mode={Mode})",
                _bus.CurrentMode
            );
            _bus.Publish(
                new BeaconEvent
                {
                    EventType = BeaconEventType.Clear,
                    Source = AgentSource.Unknown,
                    SessionId = BeaconEvent.BroadcastSessionId,
                    HookEvent = "FocusGained",
                    Reason = "VS Code gained focus",
                }
            );
        }
    }

    public override void Dispose()
    {
        _monitor.AppFocusGained -= OnAppFocusGained;
        _monitor.Dispose();
        base.Dispose();
    }
}
