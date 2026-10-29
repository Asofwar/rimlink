using Xunit;

namespace Rimlink.BridgeTools.Tests;

public class FlowGuardTests
{
    [Fact]
    public void SecondFlowIsRefusedWhileOneRuns()
    {
        var guard = new FlowGuard();
        Assert.True(guard.TryEnter("connect"));
        Assert.False(guard.TryEnter("start_colony"));
        Assert.Equal("connect", guard.Running);
        guard.Exit();
        Assert.True(guard.TryEnter("start_colony"));
    }

    [Fact]
    public void GuardIsHeldUntilAnAbandonedFlowEnds()
    {
        // A timed-out connect leaves while its login flow still runs: a second login must not start before it ends.
        var guard = new FlowGuard();
        Assert.True(guard.TryEnter("connect"));
        var login = new TaskCompletionSource();
        guard.ExitWhen(login.Task);
        Assert.False(guard.TryEnter("connect"));
        login.SetResult();
        Assert.Null(guard.Running);
        Assert.True(guard.TryEnter("connect"));
    }
}
