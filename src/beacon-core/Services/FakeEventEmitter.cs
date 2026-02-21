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

        var sequence = new BeaconEvent[]
        {
            new()
            {
                EventType = BeaconEventType.Waiting,
                Source = AgentSource.Unknown,
                HookEvent = "Fake",
                Reason = "[fake] Agent is waiting for approval",
            },
            new()
            {
                EventType = BeaconEventType.Done,
                Source = AgentSource.Unknown,
                HookEvent = "Fake",
                Reason = "[fake] Agent has finished",
            },
            new()
            {
                EventType = BeaconEventType.Clear,
                Source = AgentSource.Unknown,
                HookEvent = "Fake",
                Reason = "[fake] User returned",
            },
        };

        var index = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(10_000, stoppingToken);

            var evt = sequence[index % sequence.Length];
            _logger.LogInformation("Emitting fake event: {Event}", evt.EventType);
            _bus.Publish(evt);
            index++;
        }
    }
}
