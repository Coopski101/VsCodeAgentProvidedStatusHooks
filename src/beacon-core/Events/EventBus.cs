using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace BeaconCore.Events;

public enum BeaconMode
{
    Idle,
    Waiting,
    Done,
}

public sealed class SessionState
{
    public BeaconMode Mode { get; set; } = BeaconMode.Idle;
    public AgentSource? Source { get; set; }
    public DateTimeOffset LastUpdated { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class EventBus
{
    private readonly List<Channel<BeaconEvent>> _subscribers = [];
    private readonly Lock _lock = new();
    private readonly ILogger<EventBus> _logger;
    private readonly Dictionary<string, SessionState> _sessions = new(
        StringComparer.OrdinalIgnoreCase
    );

    public EventBus(ILogger<EventBus> logger)
    {
        _logger = logger;
    }

    public BeaconMode CurrentMode
    {
        get
        {
            lock (_lock)
            {
                var hasWaiting = false;
                var hasDone = false;
                foreach (var s in _sessions.Values)
                {
                    if (s.Mode == BeaconMode.Waiting)
                        hasWaiting = true;
                    else if (s.Mode == BeaconMode.Done)
                        hasDone = true;
                }
                if (hasWaiting)
                    return BeaconMode.Waiting;
                if (hasDone)
                    return BeaconMode.Done;
                return BeaconMode.Idle;
            }
        }
    }

    public Dictionary<string, SessionState> GetSessions()
    {
        lock (_lock)
        {
            return new Dictionary<string, SessionState>(
                _sessions,
                StringComparer.OrdinalIgnoreCase
            );
        }
    }

    public ChannelReader<BeaconEvent> Subscribe()
    {
        var channel = Channel.CreateUnbounded<BeaconEvent>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false }
        );

        lock (_lock)
        {
            _subscribers.Add(channel);
        }

        return channel.Reader;
    }

    public void Unsubscribe(ChannelReader<BeaconEvent> reader)
    {
        lock (_lock)
        {
            _subscribers.RemoveAll(ch => ch.Reader == reader);
        }
    }

    public void Publish(BeaconEvent evt)
    {
        lock (_lock)
        {
            if (evt.SessionId == BeaconEvent.BroadcastSessionId)
            {
                if (evt.EventType == BeaconEventType.Clear)
                {
                    _logger.LogInformation(
                        "[{SessionId}] Broadcast Clear — resetting all {Count} session(s)",
                        evt.SessionId,
                        _sessions.Count
                    );
                    foreach (var s in _sessions.Values)
                    {
                        s.Mode = BeaconMode.Idle;
                        s.Source = null;
                        s.LastUpdated = DateTimeOffset.UtcNow;
                    }
                }
            }
            else
            {
                if (!_sessions.TryGetValue(evt.SessionId, out var session))
                {
                    session = new SessionState();
                    _sessions[evt.SessionId] = session;
                    _logger.LogInformation(
                        "[{SessionId}] New session registered",
                        evt.SessionId[..Math.Min(8, evt.SessionId.Length)]
                    );
                }

                if (evt.EventType is BeaconEventType.Waiting or BeaconEventType.Done)
                {
                    session.Mode =
                        evt.EventType == BeaconEventType.Waiting
                            ? BeaconMode.Waiting
                            : BeaconMode.Done;
                    session.Source = evt.Source;
                    session.LastUpdated = DateTimeOffset.UtcNow;

                    _logger.LogInformation(
                        "[{SessionId}] {EventType} from {Source} — aggregate={Aggregate}",
                        evt.SessionId[..Math.Min(8, evt.SessionId.Length)],
                        evt.EventType,
                        evt.Source,
                        CurrentMode
                    );
                }
                else if (evt.EventType == BeaconEventType.Clear)
                {
                    if (session.Mode == BeaconMode.Idle)
                        return;
                    session.Mode = BeaconMode.Idle;
                    session.Source = null;
                    session.LastUpdated = DateTimeOffset.UtcNow;

                    _logger.LogInformation(
                        "[{SessionId}] Clear — aggregate={Aggregate}",
                        evt.SessionId[..Math.Min(8, evt.SessionId.Length)],
                        CurrentMode
                    );
                }
            }

            foreach (var ch in _subscribers)
            {
                ch.Writer.TryWrite(evt);
            }
        }
    }
}
