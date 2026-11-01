using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Rimlink.Client.Colonies;
using Rimlink.Client.Net;
using Rimlink.Client.Tests.Support;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Live;
using Rimlink.Server.Storage;
using Rimlink.Server.Tests.Support;
using Xunit;

namespace Rimlink.Client.Tests.Colonies;

/// <summary>Real coop RPCs and two live streams; relaying a command/result does not prove execution by a game pawn.</summary>
public sealed class ColonyStreamTests
{
    private static ServerFixture Server() => LiveFixtures.LiveServer().WithSetting("Auth:AllowPassword", "true");

    private static async Task AtHome(RimlinkConnection connection, CancellationToken ct) =>
        await connection.Auth.SetAtHomeAsync(new AtHomeRequest { AtHome = true, CoopCapable = true }, cancellationToken: ct);

    private static async Task Grant(RimlinkConnection owner, RimlinkConnection member, CancellationToken ct)
    {
        await AtHome(owner, ct);
        await AtHome(member, ct);
        var ownersApi = new ColonyClient(owner);
        await ownersApi.Invite(member.Login!.Player.PlayerId, ct);
        await new ColonyClient(member).Answer(owner.Login!.Player.PlayerId, true, ct);
        await ownersApi.Assign(member.Login.Player.PlayerId, new long[] { 12, 17 }, ct);
    }

    private static async Task<LiveServerMsg> Read(
        AsyncDuplexStreamingCall<LiveClientMsg, LiveServerMsg> stream, LiveServerMsg.MOneofCase kind, CancellationToken ct)
    {
        while (await stream.ResponseStream.MoveNext(ct))
        {
            var frame = stream.ResponseStream.Current;
            if (frame.MCase == kind) return frame;
            Assert.NotEqual(LiveServerMsg.MOneofCase.End, frame.MCase);
        }
        throw new InvalidOperationException($"Live stream closed before {kind}");
    }

    private static async Task Hellos(
        AsyncDuplexStreamingCall<LiveClientMsg, LiveServerMsg> owner,
        AsyncDuplexStreamingCall<LiveClientMsg, LiveServerMsg> member, long sessionId, CancellationToken ct)
    {
        var ownerHello = new Hello { Role = LiveRelay.HostRole, SnapshotSeq = 11 };
        var memberHello = new Hello { Role = LiveRelay.GuestRole, SnapshotSeq = 22 };
        await owner.RequestStream.WriteAsync(new LiveClientMsg { SessionId = sessionId, Hello = ownerHello }, ct);
        await member.RequestStream.WriteAsync(new LiveClientMsg { SessionId = sessionId, Hello = memberHello }, ct);
        Assert.Equal(memberHello, (await Read(owner, LiveServerMsg.MOneofCase.PeerHello, ct)).PeerHello);
        // The host's first Hello may precede the guest's attachment; now the received guest Hello proves both are attached.
        await owner.RequestStream.WriteAsync(new LiveClientMsg { SessionId = sessionId, Hello = ownerHello }, ct);
        Assert.Equal(ownerHello, (await Read(member, LiveServerMsg.MOneofCase.PeerHello, ct)).PeerHello);
    }

    private static async Task OrderReachesOwner(
        AsyncDuplexStreamingCall<LiveClientMsg, LiveServerMsg> owner,
        AsyncDuplexStreamingCall<LiveClientMsg, LiveServerMsg> member, long sessionId, CancellationToken ct)
    {
        var order = new Order { Seq = 1, PawnId = 12, Draft = true };
        await member.RequestStream.WriteAsync(new LiveClientMsg { SessionId = sessionId, Order = order }, ct);
        Assert.Equal(order, (await Read(owner, LiveServerMsg.MOneofCase.Order, ct)).Order);
        var result = new OrderResult { Seq = order.Seq, Ok = true };
        await owner.RequestStream.WriteAsync(new LiveClientMsg { SessionId = sessionId, OrderResult = result }, ct);
        Assert.Equal(result, (await Read(member, LiveServerMsg.MOneofCase.OrderResult, ct)).OrderResult);
    }

    private static async Task NoEscrowRows(ServerFixture server, CancellationToken ct)
    {
        using var scope = server.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RimlinkDbContext>();
        Assert.Empty(await db.Parcels.AsNoTracking().ToListAsync(ct));
        Assert.Empty(await db.Raids.AsNoTracking().ToListAsync(ct));
    }

    [Fact]
    public async Task BothPeersReceiveHelloAndGuestOrderReachesOwnerWithoutRaidEscrow()
    {
        using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var server = Server();
        await using var owner = await ClientFixtures.Connected(server, "owner");
        await using var member = await ClientFixtures.Connected(server, "member");
        await Grant(owner, member, ct.Token);
        var session = await new ColonyClient(member).Join(owner.Login!.Player.PlayerId, "coop-stream", ct.Token);
        Assert.Equal(LiveKind.LiveCoop, session.Kind);
        Assert.Equal(new long[] { 12, 17 }, session.ColonyPawnIds);
        using var ownersStream = owner.LiveApi.Stream(cancellationToken: ct.Token);
        using var membersStream = member.LiveApi.Stream(cancellationToken: ct.Token);

        await Hellos(ownersStream, membersStream, session.Id, ct.Token);
        await OrderReachesOwner(ownersStream, membersStream, session.Id, ct.Token);

        var running = await owner.LiveApi.CurrentAsync(new Empty(), cancellationToken: ct.Token);
        Assert.Equal(session.Id, running.Id);
        Assert.Equal(LiveState.LiveRunning, running.State);
        Assert.Empty((await owner.LiveApi.PawnsAsync(new LiveRef { SessionId = session.Id }, cancellationToken: ct.Token)).Pawns);
        await NoEscrowRows(server, ct.Token);
    }

    private static async Task RefusedAttach(RimlinkConnection connection, long sessionId, int role, CancellationToken ct)
    {
        using var stream = connection.LiveApi.Stream(cancellationToken: ct);
        var failure = await Assert.ThrowsAsync<RpcException>(async () =>
        {
            await stream.RequestStream.WriteAsync(new LiveClientMsg { SessionId = sessionId, Hello = new Hello { Role = role } }, ct);
            await stream.ResponseStream.MoveNext(ct);
        });
        Assert.Equal(StatusCode.FailedPrecondition, failure.StatusCode);
        Assert.Equal(ErrorCode.LiveGone, failure.ErrorDetail()?.Code);
    }

    private static async Task ClosedWithoutOrders(AsyncDuplexStreamingCall<LiveClientMsg, LiveServerMsg> stream, CancellationToken ct)
    {
        while (await stream.ResponseStream.MoveNext(ct))
            Assert.NotEqual(LiveServerMsg.MOneofCase.Order, stream.ResponseStream.Current.MCase);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RevokeOrAssignmentChangeEndsBothStreamsAndRejectsFurtherAttachmentAndOrders(bool changeAssignment)
    {
        using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var server = Server();
        await using var owner = await ClientFixtures.Connected(server, "owner");
        await using var member = await ClientFixtures.Connected(server, "member");
        await Grant(owner, member, ct.Token);
        long ownerId = owner.Login!.Player.PlayerId, memberId = member.Login!.Player.PlayerId;
        var membersApi = new ColonyClient(member);
        var session = await membersApi.Join(ownerId, "coop-ended", ct.Token);
        using var ownersStream = owner.LiveApi.Stream(cancellationToken: ct.Token);
        using var membersStream = member.LiveApi.Stream(cancellationToken: ct.Token);
        await Hellos(ownersStream, membersStream, session.Id, ct.Token);
        await OrderReachesOwner(ownersStream, membersStream, session.Id, ct.Token);

        var ownersApi = new ColonyClient(owner);
        if (changeAssignment) await ownersApi.Assign(memberId, new long[] { 23 }, ct.Token);
        else await ownersApi.Revoke(ownerId, memberId, ct.Token);

        var ends = await Task.WhenAll(Read(ownersStream, LiveServerMsg.MOneofCase.End, ct.Token), Read(membersStream, LiveServerMsg.MOneofCase.End, ct.Token));
        Assert.All(ends, frame => Assert.Equal(LiveEndReason.EndHostAborted, frame.End.Reason));
        var ended = await owner.LiveApi.CurrentAsync(new Empty(), cancellationToken: ct.Token);
        Assert.Equal((session.Id, LiveState.LiveEnding), (ended.Id, ended.State));
        // An accepted write is only a transport buffer: the closed owner's response must never contain this later order.
        try
        {
            await membersStream.RequestStream.WriteAsync(new LiveClientMsg { SessionId = session.Id, Order = new Order { Seq = 2, PawnId = 12, Draft = false } }, ct.Token);
        }
        catch (RpcException) { }
        catch (InvalidOperationException) { }
        await Task.WhenAll(ClosedWithoutOrders(ownersStream, ct.Token), ClosedWithoutOrders(membersStream, ct.Token));
        await RefusedAttach(owner, session.Id, LiveRelay.HostRole, ct.Token);
        await RefusedAttach(member, session.Id, LiveRelay.GuestRole, ct.Token);
        var retry = await membersApi.Join(ownerId, "coop-ended", ct.Token);
        Assert.Equal((session.Id, LiveState.LiveEnding), (retry.Id, retry.State));
        var grants = (await membersApi.List(ct.Token)).Entries;
        if (changeAssignment) Assert.Equal(new long[] { 23 }, Assert.Single(grants).PawnIds);
        else
        {
            Assert.Empty(grants);
            var refused = await Assert.ThrowsAsync<RpcException>(() => membersApi.Join(ownerId, "new-after-revoke", ct.Token));
            Assert.Equal(StatusCode.PermissionDenied, refused.StatusCode);
        }
        await NoEscrowRows(server, ct.Token);
        using var scope = server.Services.CreateScope();
        Assert.Single(await scope.ServiceProvider.GetRequiredService<RimlinkDbContext>().LiveSessions.AsNoTracking().ToListAsync(ct.Token));
    }

    [Fact]
    public async Task ClientJoinRetryKeepsTheOriginalOwnerAndSessionIdentity()
    {
        using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var server = Server();
        await using var owner = await ClientFixtures.Connected(server, "owner");
        await using var member = await ClientFixtures.Connected(server, "member");
        await using var otherOwner = await ClientFixtures.Connected(server, "other-owner");
        await Grant(owner, member, ct.Token);
        await Grant(otherOwner, member, ct.Token);
        var api = new ColonyClient(member);
        var original = await api.Join(owner.Login!.Player.PlayerId, "join-identity", ct.Token);

        var changed = await Assert.ThrowsAsync<RpcException>(() => api.Join(otherOwner.Login!.Player.PlayerId, "join-identity", ct.Token));
        Assert.Equal(StatusCode.InvalidArgument, changed.StatusCode);
        var replayed = await api.Join(owner.Login.Player.PlayerId, "join-identity", ct.Token);

        Assert.Equal(original, replayed);
        await NoEscrowRows(server, ct.Token);
        using var scope = server.Services.CreateScope();
        Assert.Equal(original.Id, Assert.Single(await scope.ServiceProvider.GetRequiredService<RimlinkDbContext>().LiveSessions.AsNoTracking().ToListAsync(ct.Token)).Id);
    }

    [Fact]
    public async Task AcceptedMembershipAndAssignmentsSurviveServerRestartAndStillOpenWorkingStreams()
    {
        using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var first = Server().KeepDataDir();
        // The restarted fixture owns cleanup even if an assertion in the first server fails; it must not seed a second world.
        using var restarted = Server().WithDataDir(first.DataDir).WithoutWorld();
        long ownerId, memberId;
        using (first)
        {
            await using var owner = await ClientFixtures.Connected(first, "owner");
            await using var member = await ClientFixtures.Connected(first, "member");
            await Grant(owner, member, ct.Token);
            ownerId = owner.Login!.Player.PlayerId;
            memberId = member.Login!.Player.PlayerId;
            await NoEscrowRows(first, ct.Token);
        }

        await using var ownerAfterRestart = ClientFixtures.Connect(restarted, "owner");
        await using var memberAfterRestart = ClientFixtures.Connect(restarted, "member");
        await ownerAfterRestart.ConnectAsync(ct.Token);
        await memberAfterRestart.ConnectAsync(ct.Token);
        await AtHome(ownerAfterRestart, ct.Token);
        await AtHome(memberAfterRestart, ct.Token);
        var api = new ColonyClient(memberAfterRestart);
        var access = Assert.Single((await api.List(ct.Token)).Entries);
        Assert.Equal((ownerId, memberId, true), (access.Owner.PlayerId, access.Member.PlayerId, access.Accepted));
        Assert.Equal(new long[] { 12, 17 }, access.PawnIds);
        Assert.Equal(access, Assert.Single((await new ColonyClient(ownerAfterRestart).List(ct.Token)).Entries));
        var session = await api.Join(ownerId, "join-after-restart", ct.Token);
        Assert.Equal(LiveKind.LiveCoop, session.Kind);
        Assert.Equal(new long[] { 12, 17 }, session.ColonyPawnIds);
        using var ownersStream = ownerAfterRestart.LiveApi.Stream(cancellationToken: ct.Token);
        using var membersStream = memberAfterRestart.LiveApi.Stream(cancellationToken: ct.Token);

        await Hellos(ownersStream, membersStream, session.Id, ct.Token);
        await OrderReachesOwner(ownersStream, membersStream, session.Id, ct.Token);
        await NoEscrowRows(restarted, ct.Token);
    }
}
