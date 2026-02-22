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
            foreach (var ch in _subscribers)
            {
                ch.Writer.TryWrite(evt);
            }
        }
    }
}
