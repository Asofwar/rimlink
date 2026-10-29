using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Rimlink.Protocol.V1;
using Rimlink.Server.Storage;
using Rimlink.Server.Storage.Entities;
using Rimlink.Server.Tests.Support;
using Xunit;
using static Rimlink.Server.Tests.Support.EconomyFixtures;
using static Rimlink.Server.Tests.Support.GrpcTestExtensions;
using static Rimlink.Server.Tests.Support.LiveFixtures;
using static Rimlink.Server.Tests.Support.RaidFixtures;

namespace Rimlink.Server.Tests.Live;

/// <summary>Spec §2: propose, invite, accept, decline, fallback, cancel, timeouts.</summary>
public class LiveSessionTests
{
    private sealed record Pair(ServerFixture Fixture, LiveClients Nova, LiveClients Bob);

    private static async Task<Pair> NovaAndBob(ServerFixture? fixture = null, bool bobAtHome = true)
    {
        fixture ??= LiveServer();
        var nova = await Connected(fixture, "nova");
        var bob = await Connected(fixture, "bob", atHome: bobAtHome);
        await MakeTargetRaidable(fixture, bob.PlayerId);
        return new Pair(fixture, nova, bob);
    }

    private static async Task<T> Db<T>(ServerFixture fixture, Func<RimlinkDbContext, Task<T>> query)
    {
        using var scope = fixture.Services.CreateScope();
        return await query(scope.ServiceProvider.GetRequiredService<RimlinkDbContext>());
    }

    [Fact]
    public async Task ProposeInvitesAnOnlineHost()
    {
        var p = await NovaAndBob();
        using var fixture = p.Fixture;
        using var bobEvents = p.Bob.Subscribe();

        var session = await p.Nova.Live.ProposeAsync(Propose(p.Bob.PlayerId, LiveKind.LiveRaid), p.Nova.Auth);

        Assert.Equal(LiveState.LiveProposed, session.State);
        Assert.Equal(LiveKind.LiveRaid, session.Kind);
        Assert.NotEqual(0, session.RaidId);
        var invite = await ReadEvent(bobEvents, ServerEvent.BodyOneofCase.LiveInvite, e => e.LiveInvite);
        Assert.Equal(session.Id, invite.Session.Id);
        Assert.Equal(2, invite.InviteSeconds);
        Assert.Equal("Nova", invite.Session.Guest.Name);
        Assert.Equal(100, await Db(fixture, db => db.Players.Where(x => x.Id == p.Nova.PlayerId).Select(x => x.LastOutgoingTick).SingleAsync()));
        var raid = await Db(fixture, db => db.Raids.SingleAsync(r => r.Id == session.RaidId));
        Assert.Equal(RaidStateDb.Queued, raid.State);
        Assert.Equal((int)RaidController.ControllerLive, raid.Controller);
        Assert.Empty((await p.Bob.Raids.PendingAsync(new Empty(), p.Bob.Auth)).Raids); // not a 3a raid while the live session is open
        Assert.Equal(session.Id, (await p.Nova.Live.CurrentAsync(new Empty(), p.Nova.Auth)).Id);
    }

    [Fact]
    public async Task ProposeToAnOfflineOrAwayHostIsHostOffline()
    {
        var p = await NovaAndBob(bobAtHome: false);
        using var fixture = p.Fixture;

        var away = await Assert.ThrowsAsync<RpcException>(() => p.Nova.Live.ProposeAsync(Propose(p.Bob.PlayerId, LiveKind.LiveRaid), p.Nova.Auth).ResponseAsync);
        Assert.Equal(ErrorCode.HostOffline, away.ErrorDetail()!.Code);

        await p.Bob.AuthApi.LogoutAsync(new Empty(), p.Bob.Auth);
        var offline = await Assert.ThrowsAsync<RpcException>(() => p.Nova.Live.ProposeAsync(Propose(p.Bob.PlayerId, LiveKind.LiveVisit), p.Nova.Auth).ResponseAsync);
        Assert.Equal(ErrorCode.HostOffline, offline.ErrorDetail()!.Code);
        Assert.Empty((await p.Nova.Mailbox.ListAsync(new Empty(), p.Nova.Auth)).Parcels); // nothing was escrowed
    }

    [Fact]
    public async Task AutoAcceptStartsAtOnce()
    {
        var p = await NovaAndBob();
        using var fixture = p.Fixture;
        var flags = await p.Bob.Live.SetAutoAcceptAsync(new AutoAcceptRequest { Enabled = true }, p.Bob.Auth);
        Assert.True(flags.AutoAcceptLive);
        using var bobEvents = p.Bob.Subscribe();
        using var novaEvents = p.Nova.Subscribe();

        var session = await p.Nova.Live.ProposeAsync(Propose(p.Bob.PlayerId, LiveKind.LiveVisit), p.Nova.Auth);

        Assert.Equal(LiveState.LiveStarting, session.State);
        Assert.Equal(session.Id, (await ReadEvent(bobEvents, ServerEvent.BodyOneofCase.LiveStarted, e => e.LiveStarted)).Session.Id);
        Assert.Equal(session.Id, (await ReadEvent(novaEvents, ServerEvent.BodyOneofCase.LiveStarted, e => e.LiveStarted)).Session.Id);
        Assert.Equal(LiveState.LiveStarting, (await p.Bob.Live.CurrentAsync(new Empty(), p.Bob.Auth)).State);
    }

    [Fact]
    public async Task AcceptStartsDeclineFallsBack()
    {
        var p = await NovaAndBob();
        using var fixture = p.Fixture;
        using var novaEvents = p.Nova.Subscribe();
        using var bobEvents = p.Bob.Subscribe();

        var first = await p.Nova.Live.ProposeAsync(Propose(p.Bob.PlayerId, LiveKind.LiveRaid), p.Nova.Auth);
        var declined = await p.Bob.Live.RespondAsync(new LiveAnswer { SessionId = first.Id, Accept = false }, p.Bob.Auth);

        Assert.Equal(LiveState.LiveFallback, declined.State);
        Assert.Equal(first.Id, (await ReadEvent(novaEvents, ServerEvent.BodyOneofCase.LiveFallback, e => e.LiveFallback)).Session.Id);
        Assert.Equal(first.RaidId, (await ReadEvent(bobEvents, ServerEvent.BodyOneofCase.RaidQueued, e => e.RaidQueued)).Raid.Id); // the 3a pipeline takes over
        Assert.Equal((int)RaidController.ControllerAi, (await Db(fixture, db => db.Raids.SingleAsync(r => r.Id == first.RaidId))).Controller);
        Assert.Equal(first.RaidId, Assert.Single((await p.Bob.Raids.PendingAsync(new Empty(), p.Bob.Auth)).Raids).Id);
        var gone = await Assert.ThrowsAsync<RpcException>(() => p.Bob.Live.RespondAsync(new LiveAnswer { SessionId = first.Id, Accept = true }, p.Bob.Auth).ResponseAsync);
        Assert.Equal(ErrorCode.LiveGone, gone.ErrorDetail()!.Code);

        // The fallen-back raid holds nova's active-raid slot; a visit does not need one.
        var second = await p.Nova.Live.ProposeAsync(Propose(p.Bob.PlayerId, LiveKind.LiveVisit), p.Nova.Auth);
        var accepted = await p.Bob.Live.RespondAsync(new LiveAnswer { SessionId = second.Id, Accept = true }, p.Bob.Auth);
        Assert.Equal(LiveState.LiveStarting, accepted.State);
        Assert.Equal(second.Id, (await ReadEvent(novaEvents, ServerEvent.BodyOneofCase.LiveStarted, e => e.LiveStarted)).Session.Id);
    }

    [Fact]
    public async Task InviteTimeoutFallsBack()
    {
        var p = await NovaAndBob();
        using var fixture = p.Fixture;
        using var novaEvents = p.Nova.Subscribe();

        var session = await p.Nova.Live.ProposeAsync(Propose(p.Bob.PlayerId, LiveKind.LiveRaid), p.Nova.Auth);

        Assert.Equal(session.Id, (await ReadEvent(novaEvents, ServerEvent.BodyOneofCase.LiveFallback, e => e.LiveFallback, TimeSpan.FromSeconds(10))).Session.Id);
        Assert.Equal(LiveStateDb.Fallback, (await Db(fixture, db => db.LiveSessions.SingleAsync(s => s.Id == session.Id))).State);
    }

    [Fact]
    public async Task SecondProposeIsLiveActive()
    {
        var p = await NovaAndBob();
        using var fixture = p.Fixture;
        var proposal = Propose(p.Bob.PlayerId, LiveKind.LiveRaid);

        var session = await p.Nova.Live.ProposeAsync(proposal, p.Nova.Auth);
        var again = await p.Nova.Live.ProposeAsync(proposal, p.Nova.Auth); // the same request replayed
        Assert.Equal(session.Id, again.Id);

        var other = await Assert.ThrowsAsync<RpcException>(() => p.Nova.Live.ProposeAsync(Propose(p.Bob.PlayerId, LiveKind.LiveVisit), p.Nova.Auth).ResponseAsync);
        Assert.Equal(ErrorCode.LiveActive, other.ErrorDetail()!.Code);
        Assert.Equal(1, await Db(fixture, db => db.LiveSessions.CountAsync()));
        Assert.Equal(1, await Db(fixture, db => db.Raids.CountAsync()));

        // The host is busy too: a third player cannot propose to bob meanwhile.
        var carl = await Connected(fixture, "carl");
        var busy = await Assert.ThrowsAsync<RpcException>(() => carl.Live.ProposeAsync(Propose(p.Bob.PlayerId, LiveKind.LiveVisit), carl.Auth).ResponseAsync);
        Assert.Equal(ErrorCode.LiveActive, busy.ErrorDetail()!.Code);
    }

    [Fact]
    public async Task VisitFallbackReturnsThePawns()
    {
        var p = await NovaAndBob();
        using var fixture = p.Fixture;

        var session = await p.Nova.Live.ProposeAsync(Propose(p.Bob.PlayerId, LiveKind.LiveVisit, pawns: 3), p.Nova.Auth);
        Assert.Equal(0, session.RaidId);
        await p.Bob.Live.RespondAsync(new LiveAnswer { SessionId = session.Id, Accept = false }, p.Bob.Auth);

        var parcel = Assert.Single((await p.Nova.Mailbox.ListAsync(new Empty(), p.Nova.Auth)).Parcels);
        Assert.Equal(ParcelKind.ParcelRaidReturn, parcel.Kind);
        Assert.Equal(3, parcel.Pawns.Pawns.Count);
    }

    [Fact]
    public async Task GuestCancelBeforeStartReturnsThePawns()
    {
        var p = await NovaAndBob();
        using var fixture = p.Fixture;
        var session = await p.Nova.Live.ProposeAsync(Propose(p.Bob.PlayerId, LiveKind.LiveRaid), p.Nova.Auth);

        await p.Nova.Live.CancelAsync(new LiveRef { SessionId = session.Id }, p.Nova.Auth);

        Assert.Equal(RaidStateDb.Cancelled, (await Db(fixture, db => db.Raids.SingleAsync(r => r.Id == session.RaidId))).State);
        Assert.Equal(ParcelKind.ParcelRaidReturn, Assert.Single((await p.Nova.Mailbox.ListAsync(new Empty(), p.Nova.Auth)).Parcels).Kind);
        var row = await Db(fixture, db => db.LiveSessions.SingleAsync(s => s.Id == session.Id));
        Assert.Equal(LiveStateDb.Ended, row.State);
        Assert.Equal((int)LiveEndReason.EndGuestCancelled, row.EndReason);
        var gone = await Assert.ThrowsAsync<RpcException>(() => p.Nova.Live.CurrentAsync(new Empty(), p.Nova.Auth).ResponseAsync);
        Assert.Equal(ErrorCode.LiveGone, gone.ErrorDetail()!.Code);
    }

    [Fact]
    public async Task HostAbortEnds()
    {
        var p = await NovaAndBob();
        using var fixture = p.Fixture;
        await p.Bob.Live.SetAutoAcceptAsync(new AutoAcceptRequest { Enabled = true }, p.Bob.Auth);
        using var novaEvents = p.Nova.Subscribe();
        var session = await p.Nova.Live.ProposeAsync(Propose(p.Bob.PlayerId, LiveKind.LiveRaid), p.Nova.Auth);

        await p.Bob.Live.CancelAsync(new LiveRef { SessionId = session.Id }, p.Bob.Auth);

        var ended = await ReadEvent(novaEvents, ServerEvent.BodyOneofCase.LiveEnded, e => e.LiveEnded);
        Assert.Equal(LiveState.LiveEnding, ended.Session.State);
        Assert.Equal(LiveEndReason.EndHostAborted, ended.Session.EndReason);
        Assert.Equal(LiveState.LiveEnding, (await p.Bob.Live.CurrentAsync(new Empty(), p.Bob.Auth)).State); // the host still owes the report
        Assert.Empty((await p.Nova.Mailbox.ListAsync(new Empty(), p.Nova.Auth)).Parcels); // the pawns come with the host's report, not now
    }

    [Fact]
    public async Task PeacefulAndCooldownRulesApply()
    {
        var p = await NovaAndBob(LiveServer().WithSetting("Raids:TargetCooldownHours", "8"));
        using var fixture = p.Fixture;
        await p.Bob.Raids.SetPeacefulAsync(new PeacefulRequest { Peaceful = true }, p.Bob.Auth);

        var peaceful = await Assert.ThrowsAsync<RpcException>(() => p.Nova.Live.ProposeAsync(Propose(p.Bob.PlayerId, LiveKind.LiveRaid), p.Nova.Auth).ResponseAsync);
        Assert.Equal(ErrorCode.PeacefulMode, peaceful.ErrorDetail()!.Code);
        await p.Nova.Live.ProposeAsync(Propose(p.Bob.PlayerId, LiveKind.LiveVisit), p.Nova.Auth); // peaceful hosts still receive visitors

        // Leaving peaceful mode takes a day, so the cooldown case uses another host: carl was just raided the 3a way.
        var carl = await Connected(fixture, "carl");
        await MakeTargetRaidable(fixture, carl.PlayerId);
        var dave = await Connected(fixture, "dave");
        var davesRaid = await RaidClientsOf(fixture, dave.Login.SessionToken).Raids.LaunchAsync(Launch(carl.PlayerId), dave.Auth);
        Assert.NotEqual(0, davesRaid.Id);
        var eve = await Connected(fixture, "eve");
        var cooldown = await Assert.ThrowsAsync<RpcException>(() => eve.Live.ProposeAsync(Propose(carl.PlayerId, LiveKind.LiveRaid), eve.Auth).ResponseAsync);
        Assert.Equal(ErrorCode.RaidCooldown, cooldown.ErrorDetail()!.Code);
    }

    [Fact]
    public async Task DisabledLiveRefusesProposals()
    {
        var p = await NovaAndBob(LiveServer().WithSetting("Live:Enabled", "false"));
        using var fixture = p.Fixture;

        var ex = await Assert.ThrowsAsync<RpcException>(() => p.Nova.Live.ProposeAsync(Propose(p.Bob.PlayerId, LiveKind.LiveRaid), p.Nova.Auth).ResponseAsync);
        Assert.Equal(ErrorCode.LiveDisabled, ex.ErrorDetail()!.Code);
    }
}
