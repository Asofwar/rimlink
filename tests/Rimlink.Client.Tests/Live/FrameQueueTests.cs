using Rimlink.Client.Live;
using Rimlink.Protocol.V1;
using Xunit;

namespace Rimlink.Client.Tests.Live;

/// <summary>The bounded queue between the stream and the game: a delta it drops is a change the mirror never sees.</summary>
public class FrameQueueTests
{
    private static LiveServerMsg DeltaAt(long tick) => new() { Delta = new Delta { Tick = tick } };

    [Fact]
    public void DroppedDeltasAreCountedUntilTaken()
    {
        var queue = new FrameQueue(capacity: 4);
        for (int i = 1; i <= 7; i++) queue.Enqueue(DeltaAt(i));

        Assert.Equal(4, queue.Count);
        Assert.Equal(3, queue.TakeDroppedDeltas());
        Assert.Equal(0, queue.TakeDroppedDeltas());
        Assert.True(queue.TryDequeue(out var first));
        Assert.Equal(4, first!.Delta.Tick); // the oldest went first
    }

    [Fact]
    public void OtherFramesAreNeverDropped()
    {
        var queue = new FrameQueue(capacity: 2);
        queue.Enqueue(new LiveServerMsg { End = new EndMsg { Reason = LiveEndReason.EndNormal } });
        queue.Enqueue(new LiveServerMsg { PeerLost = new Empty() });
        queue.Enqueue(DeltaAt(1)); // full of non-deltas: the delta itself is the one lost

        Assert.Equal(1, queue.TakeDroppedDeltas());
        Assert.True(queue.TryDequeue(out var end));
        Assert.Equal(LiveServerMsg.MOneofCase.End, end!.MCase);
    }

    [Fact]
    public void SendSaysWhichDeltasTheLocalRateDropped()
    {
        var client = new LiveClient(null!, new Rimlink.Client.Net.ConsoleLog()); // Send only queues: no connection is touched
        int accepted = 0;
        for (int i = 0; i < 30; i++) if (client.Send(DeltaFrame(i))) accepted++;

        Assert.InRange(accepted, 20, 21); // the bucket holds 20 deltas; a sender that is told can keep its diff for the next one
        Assert.True(client.Send(new LiveClientMsg { Heartbeat = new Empty() }));
    }

    private static LiveClientMsg DeltaFrame(long tick) => new() { Delta = new Delta { Tick = tick } };

    [Fact]
    public void ClearForgetsTheCount()
    {
        var queue = new FrameQueue(capacity: 1);
        queue.Enqueue(DeltaAt(1));
        queue.Enqueue(DeltaAt(2));
        queue.Clear();

        Assert.Equal(0, queue.TakeDroppedDeltas());
    }
}
