using BeaconCore.Events;

namespace BeaconCore.Services;

public sealed class FakeEventEmitter : BackgroundService
{
    private readonly EventBus _bus;
    private readonly ILogger<FakeEventEmitter> _logger;

    public FakeEventEmitter(EventBus bus, ILogger<FakeEventEmitter> logger)
    {
        _bus = bus;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("FakeEventEmitter started — cycling events every 10s");

        var sequence = new[]
        {
            (BeaconEventType.Waiting, "[fake] Agent is waiting for approval"),
            (BeaconEventType.Done, "[fake] Agent has finished"),
            (BeaconEventType.Clear, "[fake] User returned"),
        };

        var index = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(10_000, stoppingToken);

            var (eventType, reason) = sequence[index % sequence.Length];
            var evt = new BeaconEvent
            {
                EventType = eventType,
                Source = AgentSource.Unknown,
                SessionId = "fake-session",
                HookEvent = "Fake",
                Reason = reason,
            };
            _logger.LogInformation("Emitting fake event: {Event}", evt.EventType);
            _bus.Publish(evt);
            index++;
        }
    }
}
