using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;

namespace LanChat;

public sealed class EventHub
{
    private readonly ConcurrentDictionary<Guid, Channel<string>> _clients = new();
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public (Guid Id, ChannelReader<string> Reader) Subscribe()
    {
        var id = Guid.NewGuid();
        var channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
        _clients[id] = channel;
        return (id, channel.Reader);
    }

    public void Unsubscribe(Guid id)
    {
        if (_clients.TryRemove(id, out var channel))
        {
            channel.Writer.TryComplete();
        }
    }

    public void Publish(object evt)
    {
        var json = JsonSerializer.Serialize(evt, Options);
        foreach (var channel in _clients.Values)
        {
            channel.Writer.TryWrite(json);
        }
    }
}
