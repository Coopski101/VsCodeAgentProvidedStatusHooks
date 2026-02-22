using System.Threading.Channels;

namespace BeaconCore.Events;

public enum BeaconMode
{
    Idle,
    Waiting,
    Done,
}

public sealed class EventBus
{
    private readonly List<Channel<BeaconEvent>> _subscribers = [];
    private readonly Lock _lock = new();

    public BeaconMode CurrentMode { get; private set; } = BeaconMode.Idle;
    public bool ActiveSignal { get; private set; }
    public AgentSource? LastSource { get; private set; }

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
        if (evt.EventType is BeaconEventType.Waiting or BeaconEventType.Done)
        {
            ActiveSignal = true;
            CurrentMode =
                evt.EventType == BeaconEventType.Waiting ? BeaconMode.Waiting : BeaconMode.Done;
            LastSource = evt.Source;
        }
        else if (evt.EventType == BeaconEventType.Clear)
        {
            if (!ActiveSignal)
                return;
            ActiveSignal = false;
            CurrentMode = BeaconMode.Idle;
            LastSource = null;
        }

        lock (_lock)
        {
            foreach (var ch in _subscribers)
            {
                ch.Writer.TryWrite(evt);
            }
        }
    }
}
