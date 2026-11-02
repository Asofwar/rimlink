using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Rimlink.Protocol.V1;
using Rimlink.Server.Storage;
using Rimlink.Server.Tests.Support;
using Xunit;

namespace Rimlink.Server.Tests.Colonies;

public sealed class ColonyAccessTests
{
    [Fact]
    public async Task InvitationNeedsConsentAndAssignedPawnsBeforeJoining()
    {
        using var server = LiveFixtures.LiveServer();
        var host = await LiveFixtures.Connected(server, "host");
        var guest = await LiveFixtures.Connected(server, "guest");
        var api = new Protocol.V1.Colonies.ColoniesClient(server.CreateChannel());
        var invited = await api.InviteAsync(new ColonyInviteRequest { MemberPlayerId = guest.PlayerId }, host.Auth);
        Assert.False(invited.Accepted);
        var join = new ColonyJoinRequest { OwnerPlayerId = host.PlayerId, ClientRequestId = "join-once" };
        Assert.Equal(StatusCode.PermissionDenied, (await Assert.ThrowsAsync<RpcException>(() => api.JoinAsync(join, guest.Auth).ResponseAsync)).StatusCode);
        await api.AnswerAsync(new ColonyAnswerRequest { OwnerPlayerId = host.PlayerId, Accept = true }, guest.Auth);
        Assert.Equal(StatusCode.FailedPrecondition, (await Assert.ThrowsAsync<RpcException>(() => api.JoinAsync(join, guest.Auth).ResponseAsync)).StatusCode);
        await api.AssignAsync(new ColonyAssignment { MemberPlayerId = guest.PlayerId, PawnIds = { 12, 17 } }, host.Auth);
        var session = await api.JoinAsync(join, guest.Auth);
        Assert.Equal(LiveKind.LiveCoop, session.Kind);
        Assert.Equal(new long[] { 12, 17 }, session.ColonyPawnIds);
        Assert.Equal(session.Id, (await api.JoinAsync(join, guest.Auth)).Id);
        Assert.Empty((await host.Live.PawnsAsync(new LiveRef { SessionId = session.Id }, host.Auth)).Pawns);
        using var scope = server.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RimlinkDbContext>();
        Assert.Empty(await db.Parcels.ToListAsync());
        Assert.Empty(await db.Raids.ToListAsync());
    }

    [Fact]
    public async Task StrangerCannotAssignAndRevocationEndsCoopWithoutReturningHostPawns()
    {
        using var server = LiveFixtures.LiveServer();
        var host = await LiveFixtures.Connected(server, "host");
        var guest = await LiveFixtures.Connected(server, "guest");
        var other = await LiveFixtures.Connected(server, "other");
        var api = new Protocol.V1.Colonies.ColoniesClient(server.CreateChannel());
        await api.InviteAsync(new ColonyInviteRequest { MemberPlayerId = guest.PlayerId }, host.Auth);
        await api.AnswerAsync(new ColonyAnswerRequest { OwnerPlayerId = host.PlayerId, Accept = true }, guest.Auth);
        await Assert.ThrowsAsync<RpcException>(() => api.AssignAsync(new ColonyAssignment { MemberPlayerId = guest.PlayerId, PawnIds = { 42 } }, other.Auth).ResponseAsync);
        await api.AssignAsync(new ColonyAssignment { MemberPlayerId = guest.PlayerId, PawnIds = { 42 } }, host.Auth);
        var session = await api.JoinAsync(new ColonyJoinRequest { OwnerPlayerId = host.PlayerId, ClientRequestId = "one" }, guest.Auth);
        await api.RevokeAsync(new ColonyAccessRef { OwnerPlayerId = host.PlayerId, MemberPlayerId = guest.PlayerId }, host.Auth);
        var ended = await host.Live.CurrentAsync(new Empty(), host.Auth);
        Assert.Equal(LiveState.LiveEnding, ended.State);
        await host.Raids.ReportAsync(new RaidReport { LiveSessionId = session.Id, GameTick = 300 }, host.Auth);
        using var scope = server.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RimlinkDbContext>();
        Assert.Empty(await db.Parcels.ToListAsync());
        Assert.Equal(Server.Storage.Entities.LiveStateDb.Ended, (await db.LiveSessions.SingleAsync()).State);
    }

    [Fact]
    public async Task OwnerOfflineAndUnrelatedRequestIdReuseAreRefused()
    {
        using var server = LiveFixtures.LiveServer();
        var host = await LiveFixtures.Connected(server, "host");
        var guest = await LiveFixtures.Connected(server, "guest");
        var api = new Protocol.V1.Colonies.ColoniesClient(server.CreateChannel());
        await api.InviteAsync(new ColonyInviteRequest { MemberPlayerId = guest.PlayerId }, host.Auth);
        await api.AnswerAsync(new ColonyAnswerRequest { OwnerPlayerId = host.PlayerId, Accept = true }, guest.Auth);
        await api.AssignAsync(new ColonyAssignment { MemberPlayerId = guest.PlayerId, PawnIds = { 42 } }, host.Auth);
        await host.AuthApi.SetAtHomeAsync(new AtHomeRequest { AtHome = false }, host.Auth);
        var failure = await Assert.ThrowsAsync<RpcException>(() => api.JoinAsync(new ColonyJoinRequest { OwnerPlayerId = host.PlayerId, ClientRequestId = "one" }, guest.Auth).ResponseAsync);
        Assert.Equal(StatusCode.FailedPrecondition, failure.StatusCode);
        var detail = Assert.IsType<ErrorDetail>(failure.ErrorDetail());
        Assert.Equal(ErrorCode.HostOffline, detail.Code);
        Assert.Equal("Live.HostOffline", detail.Key);
        Assert.Equal(new[] { host.Login.Player.Name }, detail.Args);
        await Assert.ThrowsAsync<RpcException>(() => guest.Live.ProposeAsync(new LiveProposal { HostPlayerId = host.PlayerId, Kind = LiveKind.LiveCoop, Pawns = EconomyFixtures.Pawns(("Colonist", "Fake")) }, guest.Auth).ResponseAsync);
    }

    [Fact]
    public async Task ReplayedJoinCannotBeRetargetedOrBypassTheSingleWriterSlot()
    {
        using var server = LiveFixtures.LiveServer();
        var host = await LiveFixtures.Connected(server, "host");
        var guest = await LiveFixtures.Connected(server, "guest");
        var other = await LiveFixtures.Connected(server, "other");
        var api = new Protocol.V1.Colonies.ColoniesClient(server.CreateChannel());
        foreach (var member in new[] { guest, other })
        {
            await api.InviteAsync(new ColonyInviteRequest { MemberPlayerId = member.PlayerId }, host.Auth);
            await api.AnswerAsync(new ColonyAnswerRequest { OwnerPlayerId = host.PlayerId, Accept = true }, member.Auth);
            await api.AssignAsync(new ColonyAssignment { MemberPlayerId = member.PlayerId, PawnIds = { 42 } }, host.Auth);
        }
        var session = await api.JoinAsync(new ColonyJoinRequest { OwnerPlayerId = host.PlayerId, ClientRequestId = "one" }, guest.Auth);
        var busy = await Assert.ThrowsAsync<RpcException>(() => api.JoinAsync(new ColonyJoinRequest { OwnerPlayerId = host.PlayerId, ClientRequestId = "two" }, other.Auth).ResponseAsync);
        Assert.Equal(StatusCode.FailedPrecondition, busy.StatusCode);
        var detail = Assert.IsType<ErrorDetail>(busy.ErrorDetail());
        Assert.Equal(ErrorCode.LiveActive, detail.Code);
        Assert.Equal("Live.HostBusy", detail.Key);
        Assert.Equal(new[] { host.Login.Player.Name }, detail.Args);
        var changed = await Assert.ThrowsAsync<RpcException>(() => api.JoinAsync(new ColonyJoinRequest { OwnerPlayerId = other.PlayerId, ClientRequestId = "one" }, guest.Auth).ResponseAsync);
        Assert.Equal(StatusCode.InvalidArgument, changed.StatusCode);
        using var scope = server.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RimlinkDbContext>();
        Assert.Equal(session.Id, (await db.LiveSessions.SingleAsync()).Id);
    }

    [Fact]
    public async Task CoopReportCannotManufactureParcelsAndRepeatedEmptyReportIsHarmless()
    {
        using var server = LiveFixtures.LiveServer();
        var host = await LiveFixtures.Connected(server, "host");
        var guest = await LiveFixtures.Connected(server, "guest");
        var api = new Protocol.V1.Colonies.ColoniesClient(server.CreateChannel());
        await api.InviteAsync(new ColonyInviteRequest { MemberPlayerId = guest.PlayerId }, host.Auth);
        await api.AnswerAsync(new ColonyAnswerRequest { OwnerPlayerId = host.PlayerId, Accept = true }, guest.Auth);
        await api.AssignAsync(new ColonyAssignment { MemberPlayerId = guest.PlayerId, PawnIds = { 42 } }, host.Auth);
        var session = await api.JoinAsync(new ColonyJoinRequest { OwnerPlayerId = host.PlayerId, ClientRequestId = "one" }, guest.Auth);
        await Assert.ThrowsAsync<RpcException>(() => host.Raids.ReportAsync(LiveFixtures.AllAlive(0, session.Id, 1), host.Auth).ResponseAsync);
        var empty = new RaidReport { LiveSessionId = session.Id };
        await host.Raids.ReportAsync(empty, host.Auth);
        await host.Raids.ReportAsync(empty, host.Auth);
        using var scope = server.Services.CreateScope();
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<RimlinkDbContext>().Parcels.ToListAsync());
    }

    [Fact]
    public async Task PendingInvitesAndAssignmentsArePrivateAndBlockPreventsConsent()
    {
        using var server = LiveFixtures.LiveServer();
        var host = await LiveFixtures.Connected(server, "host");
        var guest = await LiveFixtures.Connected(server, "guest");
        var stranger = await LiveFixtures.Connected(server, "stranger");
        var channel = server.CreateChannel();
        var api = new Protocol.V1.Colonies.ColoniesClient(channel);
        await api.InviteAsync(new ColonyInviteRequest { MemberPlayerId = guest.PlayerId }, host.Auth);
        Assert.Empty((await api.ListAsync(new Empty(), stranger.Auth)).Entries);
        Assert.Single((await api.ListAsync(new Empty(), guest.Auth)).Entries);
        await Assert.ThrowsAsync<RpcException>(() => api.AnswerAsync(new ColonyAnswerRequest { OwnerPlayerId = host.PlayerId, Accept = true }, stranger.Auth).ResponseAsync);
        await new Protocol.V1.Social.SocialClient(channel).BlockAsync(new PlayerRef { PlayerId = host.PlayerId }, guest.Auth);
        await Assert.ThrowsAsync<RpcException>(() => api.AnswerAsync(new ColonyAnswerRequest { OwnerPlayerId = host.PlayerId, Accept = true }, guest.Auth).ResponseAsync);
    }

    [Fact]
    public async Task OldClientCannotReceiveACoopSessionItWouldMistakeForAVisit()
    {
        using var server = LiveFixtures.LiveServer();
        var host = await LiveFixtures.Connected(server, "host");
        var guest = await LiveFixtures.Connected(server, "guest");
        var api = new Protocol.V1.Colonies.ColoniesClient(server.CreateChannel());
        await api.InviteAsync(new ColonyInviteRequest { MemberPlayerId = guest.PlayerId }, host.Auth);
        await api.AnswerAsync(new ColonyAnswerRequest { OwnerPlayerId = host.PlayerId, Accept = true }, guest.Auth);
        await api.AssignAsync(new ColonyAssignment { MemberPlayerId = guest.PlayerId, PawnIds = { 42 } }, host.Auth);
        await host.AuthApi.SetAtHomeAsync(new AtHomeRequest { AtHome = true }, host.Auth);
        await Assert.ThrowsAsync<RpcException>(() => api.JoinAsync(new ColonyJoinRequest { OwnerPlayerId = host.PlayerId, ClientRequestId = "one" }, guest.Auth).ResponseAsync);
        using var scope = server.Services.CreateScope();
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<RimlinkDbContext>().LiveSessions.ToListAsync());
    }

    [Fact]
    public async Task RollbackClearsPawnAssignmentsButKeepsAcceptedMembership()
    {
        using var server = LiveFixtures.LiveServer();
        var host = await LiveFixtures.Connected(server, "host");
        var guest = await LiveFixtures.Connected(server, "guest");
        var api = new Protocol.V1.Colonies.ColoniesClient(server.CreateChannel());
        await api.InviteAsync(new ColonyInviteRequest { MemberPlayerId = guest.PlayerId }, host.Auth);
        await api.AnswerAsync(new ColonyAnswerRequest { OwnerPlayerId = host.PlayerId, Accept = true }, guest.Auth);
        await api.AssignAsync(new ColonyAssignment { MemberPlayerId = guest.PlayerId, PawnIds = { 42 } }, host.Auth);
        var saves = new Protocol.V1.Saves.SavesClient(server.CreateChannel());
        var bytes = SaveFixtures.MakeSave(ServerFixture.WorldSeed, "Host");
        var version = await SaveFixtures.Upload(saves, host.Login.SessionToken, bytes, SaveFixtures.HeaderFor(bytes, ServerFixture.WorldSeed, "Host"));
        using (var scope = server.Services.CreateScope())
            Assert.True(await scope.ServiceProvider.GetRequiredService<Server.Application.Saves.SaveStore>().Rollback(host.PlayerId, version.Version));
        var access = Assert.Single((await api.ListAsync(new Empty(), guest.Auth)).Entries);
        Assert.True(access.Accepted);
        Assert.Empty(access.PawnIds);
    }

    [Fact]
    public async Task SharedColonyInvitationRespectsThePublicProfilePreference()
    {
        using var server = LiveFixtures.LiveServer();
        var host = await LiveFixtures.Connected(server, "host");
        var guest = await LiveFixtures.Connected(server, "guest");
        var channel = server.CreateChannel();
        var api = new Protocol.V1.Colonies.ColoniesClient(channel);
        var community = new Protocol.V1.Community.CommunityClient(channel);
        await community.MutateAsync(new CommunityRequest { ClientRequestId = "prefs", Action = CommunityAction.ProfileUpdate, InvitationsOpen = false }, guest.Auth);
        await Assert.ThrowsAsync<RpcException>(() => api.InviteAsync(new ColonyInviteRequest { MemberPlayerId = guest.PlayerId }, host.Auth).ResponseAsync);
        Assert.Empty((await api.ListAsync(new Empty(), guest.Auth)).Entries);
    }
}
