using Rimlink.Client.Live;
using Rimlink.Client.Net;
using Rimlink.Protocol.V1;
using Xunit;

namespace Rimlink.Client.Tests.Live;

/// <summary>What LiveClient queues locally, without a stream: deltas are diffs, so a sender must know what did not go.</summary>
public class LiveClientSendTests
{
    private static LiveClient Client() => new(null!, new ConsoleLog()); // sending only queues: no connection is touched

    private static List<Delta> Frames(int n) => Enumerable.Range(0, n).Select(i => new Delta { Tick = i }).ToList();

    [Fact]
    public void SendSaysWhichDeltasTheLocalRateDropped()
    {
        var client = Client();
        int accepted = 0;
        for (int i = 0; i < 30; i++) if (client.Send(new LiveClientMsg { Delta = new Delta { Tick = i } })) accepted++;

        Assert.InRange(accepted, 20, 21); // the bucket holds 20 deltas
        Assert.True(client.Send(new LiveClientMsg { Heartbeat = new Empty() }));
    }

    [Fact]
    public void ADeltaIsQueuedWithAllItsFramesOrNotAtAll()
    {
        var client = Client();

        Assert.False(client.SendDelta(Frames(25))); // more frames than the rate allows: none of them goes
        Assert.True(client.SendDelta(Frames(5)));
        Assert.False(client.SendDelta(Frames(16))); // 15 tokens left
        Assert.True(client.SendDelta(Frames(15)));
    }

    [Fact]
    public void QueuedDeltaFramesAreNumberedInOrderAndRefusedOnesTakeNoNumber()
    {
        var client = Client();
        var first = Frames(3);
        var refused = Frames(25);
        var second = Frames(2);

        Assert.True(client.SendDelta(first));
        Assert.False(client.SendDelta(refused));
        Assert.True(client.SendDelta(second));

        Assert.Equal([1L, 2, 3, 4, 5], first.Concat(second).Select(f => f.DeltaSeq));
        Assert.All(refused, f => Assert.Equal(0, f.DeltaSeq));
    }

    [Fact]
    public void AResyncIsAskedAtMostOncePerInterval()
    {
        var client = Client();

        Assert.True(client.RequestResync("test"));
        Assert.False(client.RequestResync("test again"));
    }
}
