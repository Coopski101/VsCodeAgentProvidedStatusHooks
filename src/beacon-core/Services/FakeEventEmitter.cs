using BeaconCore.Events;
using BeaconCore.Sessions;

namespace BeaconCore.Services;

public sealed class FakeEventEmitter : BackgroundService
{
    private readonly SessionOrchestrator _orchestrator;
    private readonly ILogger<FakeEventEmitter> _logger;

    private const string FakeSessionId = "fake-session-001";

    public FakeEventEmitter(SessionOrchestrator orchestrator, ILogger<FakeEventEmitter> logger)
    {
        _orchestrator = orchestrator;
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
            _logger.LogInformation("Emitting fake event: {Event}", eventType);
            _orchestrator.HandleStateChange(
                FakeSessionId,
                AgentSource.Unknown,
                eventType,
                "Fake",
                reason
            );
            index++;
        }
    }
}
