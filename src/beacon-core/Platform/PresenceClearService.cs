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
