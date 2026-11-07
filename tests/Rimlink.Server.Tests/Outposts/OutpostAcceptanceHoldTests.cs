using Rimlink.OutpostAcceptanceHost;
using Xunit;

namespace Rimlink.Server.Tests.Outposts;

public sealed class OutpostAcceptanceHoldTests
{
    [Fact]
    public async Task Hold_binds_the_actual_operation_and_never_blocks_another_session_or_replay()
    {
        var hold = new AcceptanceHold(); var target = Target();
        hold.Arm("prepared-durable", target with { OperationId = "" });
        Assert.Null(await hold.ReachAsync("prepared-durable", target with { SessionSha256 = new('2', 64) }, default));
        var wait = hold.ReachAsync("prepared-durable", target, default);
        Assert.False(wait.IsCompleted); Assert.Equal(target, hold.Observe()!.Target);
        Assert.Throws<InvalidDataException>(() => hold.Release(target with { OperationId = "other" }, false));
        Assert.Null(await hold.ReachAsync("prepared-durable", target, default));
        hold.Release(target, true); Assert.True(await wait);
    }
    [Fact]
    public async Task Cancellation_and_disarm_end_waits_without_a_release_into_another_game()
    {
        foreach (bool disarm in new[] { false, true })
        {
            var hold = new AcceptanceHold(); var target = Target(); using var stop = new CancellationTokenSource();
            hold.Arm("receipt-save", target); var wait = hold.ReachAsync("receipt-save", target, stop.Token);
            if (disarm) hold.Disarm(); else stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
            Assert.Equal("Cancelled", hold.Observe()!.State);
            Assert.Throws<InvalidDataException>(() => hold.Release(target, false));
        }
    }
    private static AcceptanceFaultTarget Target() => new(new('1', 64), new('3', 64), Guid.NewGuid().ToString("N"), "staff");
}
