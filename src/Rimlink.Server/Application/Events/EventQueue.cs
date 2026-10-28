using System.Threading.Channels;
using Rimlink.Protocol.V1;

namespace Rimlink.Server.Application.Events;

/// <summary>Per-session outbox. Bounded: a client that stops reading is dropped instead of growing server memory.</summary>
public sealed class EventQueue
{
    public const int Capacity = 1024;

    private readonly Channel<ServerEvent> channel = Channel.CreateBounded<ServerEvent>(new BoundedChannelOptions(Capacity)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = true,
        SingleWriter = false,
    });

    /// <summary>False when the queue is full or closed — the caller decides what that means for the session.</summary>
    public bool TryWrite(ServerEvent ev) => channel.Writer.TryWrite(ev);

    public int Count => channel.Reader.Count;

    public IAsyncEnumerable<ServerEvent> ReadAll(CancellationToken cancellationToken) => channel.Reader.ReadAllAsync(cancellationToken);

    public void Complete() => channel.Writer.TryComplete();
}
