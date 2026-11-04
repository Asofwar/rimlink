using Grpc.Core;
using Rimlink.Client.Outposts;
using Rimlink.Protocol.V1;
using Rimlink.Server.Tests.Support;
using Xunit;

namespace Rimlink.Client.Tests.Outposts;

public sealed class OrdinaryReturnRpcTests
{
    [Fact]
    public async Task Authenticated_ordinary_return_is_unknown_then_one_start_then_acceptance_only()
    {
        var (fixture, request) = await OutpostClientFixture.CreateOrdinary(); using var owned = fixture;
        using var traffic = new OutpostTraffic(fixture.Server.Server.CreateHandler());
        await using var session = await OutpostTraffic.Connect(fixture, traffic);
        var client = new OutpostClient(session.Connection); client.BindScope(fixture.Scope);
        var query = new OrdinaryOperationQuery { Operation = request.Clone() };
        var unknown = await client.OrdinaryOperation(query, default);
        Assert.Equal(OrdinaryOperationStatus.Unknown, unknown.Status); Assert.Null(unknown.ReturnProof);
        var started = await client.BeginOrdinaryReturn(request, default);
        Assert.Equal(OrdinaryOperationStatus.ReturnStarted, started.Status); Assert.Equal(request, started.ReturnProof.Authorization);
        Assert.Equal(13, Assert.Single(started.ReturnProof.Payload.Items.Items).Count); Assert.Single(started.ReturnProof.Payload.Pawns.Pawns);
        Assert.Equal(started, await client.OrdinaryOperation(query, default));
        await fixture.AcceptOrdinaryReturn(request, started);
        var accepted = await client.BeginOrdinaryReturn(request, default);
        Assert.Equal(OrdinaryOperationStatus.ReturnAccepted, accepted.Status); Assert.Equal(started.ReturnProof, accepted.ReturnProof);
        Assert.Contains("/rimlink.v1.Outposts/OrdinaryOperation", traffic.Paths);
        Assert.Contains("/rimlink.v1.Outposts/BeginOrdinaryReturn", traffic.Paths);
    }

    [Fact]
    public async Task Current_authentication_cannot_send_an_old_ordinary_scope()
    {
        var (fixture, request) = await OutpostClientFixture.CreateOrdinary(); using var owned = fixture;
        using var traffic = new OutpostTraffic(fixture.Server.Server.CreateHandler());
        await using var session = await OutpostTraffic.Connect(fixture, traffic);
        var client = new OutpostClient(session.Connection); client.BindScope(fixture.Scope);
        request.Scope.SaveWriteEpoch++;
        await Assert.ThrowsAsync<InvalidDataException>(() => client.BeginOrdinaryReturn(request, default));
        Assert.DoesNotContain("/rimlink.v1.Outposts/BeginOrdinaryReturn", traffic.Paths);
    }

    [Fact]
    public async Task Disabled_capability_refuses_ordinary_return_without_creating_a_started_attempt()
    {
        var (fixture, request) = await OutpostClientFixture.CreateOrdinary(); using var owned = fixture;
        using var traffic = new OutpostTraffic(fixture.Server.Server.CreateHandler());
        await using var session = await OutpostTraffic.Connect(fixture, traffic);
        var client = new OutpostClient(session.Connection); fixture.SetAvailable(false);
        var error = await Assert.ThrowsAsync<RpcException>(() => client.BeginOrdinaryReturn(request, default));
        Assert.NotEqual(StatusCode.Unimplemented, error.StatusCode);
        fixture.SetAvailable(true);
        Assert.Equal(OrdinaryOperationStatus.Unknown, (await client.OrdinaryOperation(new() { Operation = request }, default)).Status);
    }

    [Fact]
    public async Task Cancelled_ordinary_operation_never_sends_a_mutation()
    {
        var (fixture, request) = await OutpostClientFixture.CreateOrdinary(); using var owned = fixture;
        using var traffic = new OutpostTraffic(fixture.Server.Server.CreateHandler());
        await using var session = await OutpostTraffic.Connect(fixture, traffic);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new OutpostClient(session.Connection).BeginOrdinaryReturn(request, cancellation.Token));
        Assert.DoesNotContain("/rimlink.v1.Outposts/BeginOrdinaryReturn", traffic.Paths);
    }
}
