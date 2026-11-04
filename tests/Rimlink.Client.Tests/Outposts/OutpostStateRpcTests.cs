using Rimlink.Client.Net;
using Rimlink.Client.Outposts;
using Rimlink.Client.State;
using Rimlink.Client.Tests.Support;
using Rimlink.Protocol.V1;
using Rimlink.Server.Tests.Support;
using Xunit;

namespace Rimlink.Client.Tests.Outposts;

public sealed partial class OutpostStateTests
{
    [Fact]
    public async Task Client_negotiates_outpost_support_and_reads_real_retained_custody()
    {
        using var fixture = await OutpostClientFixture.Create(); using var traffic = new OutpostTraffic(fixture.Server.Server.CreateHandler());
        await using var session = await OutpostTraffic.Connect(fixture, traffic); var connection = session.Connection;
        Assert.Equal(1u, connection.Login!.OutpostVersion);
        var value = await new OutpostClient(connection).State(new OutpostStateQuery { Scope = fixture.Scope }, default);
        Assert.Equal(fixture.Scope, value.Scope); Assert.Single(value.Outposts);
    }

    [Fact]
    public async Task Recovery_reads_outposts_without_manually_primed_scope()
    {
        using var fixture = await OutpostClientFixture.Create(); using var traffic = new OutpostTraffic(fixture.Server.Server.CreateHandler());
        await using var session = await OutpostTraffic.Connect(fixture, traffic); var connection = session.Connection;
        var state = new SharedState();
        var recovery = new RecoveryCoordinator(connection, state, null, new ConsoleLog(), update => { update(); return Task.CompletedTask; });
        Assert.True(await recovery.LoadStatesAsync(default));
        Assert.Equal(fixture.Scope, state.Outposts.Scope); Assert.Single(state.Outposts.Outposts);
        Assert.Contains("/rimlink.v1.World/Get", traffic.Paths); Assert.Contains("/rimlink.v1.Outposts/State", traffic.Paths);
    }

    [Fact]
    public async Task Current_session_change_drops_stale_update()
    {
        using var fixture = await OutpostClientFixture.Create(); using var traffic = new OutpostTraffic(fixture.Server.Server.CreateHandler());
        await using var session = await OutpostTraffic.Connect(fixture, traffic); var connection = session.Connection; var state = new SharedState(); state.Outposts.BindScope(fixture.Scope);
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); bool first = true;
        var recovery = new RecoveryCoordinator(connection, state, null, new ConsoleLog(), async update => {
            if (first) { first = false; reached.SetResult(); await release.Task; } update(); });
        var pending = recovery.LoadStatesAsync(default); await reached.Task; var old = connection.Login;
        try { connection.DropNetwork(TimeSpan.FromMilliseconds(100)); await ClientFixtures.WaitUntil(() => !ReferenceEquals(connection.Login, old)); }
        finally { release.TrySetResult(); }
        Assert.False(await pending); Assert.Empty(state.Outposts.Outposts); Assert.Equal(0, state.Outposts.AuthoritativeRevision);
    }

    [Fact]
    public void Shared_state_reset_clears_outpost_view()
    {
        var state = new SharedState(); state.Outposts.Load(Snapshot()); state.Reset(); Assert.Null(state.Outposts.Scope);
        Assert.Empty(state.Outposts.Outposts);
    }
}
