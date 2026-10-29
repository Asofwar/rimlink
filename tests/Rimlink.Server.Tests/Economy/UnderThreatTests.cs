using Google.Protobuf;
using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Economy;
using Rimlink.Server.Storage;
using Rimlink.Server.Storage.Entities;
using Rimlink.Server.Tests.Support;
using Xunit;
using static Rimlink.Server.Tests.Support.EconomyFixtures;
using static Rimlink.Server.Tests.Support.RaidFixtures;
using LiveSession = Rimlink.Server.Storage.Entities.LiveSession;
using Raid = Rimlink.Server.Storage.Entities.Raid;

namespace Rimlink.Server.Tests.Economy;

/// <summary>Nothing leaves a colony while a raid is on it (economy hardening spec §3).</summary>
public class UnderThreatTests
{
    private sealed record Trio(ServerFixture Fixture, LoginReply Nova, LoginReply Bob, LoginReply Cara, EconomyClients N, EconomyClients B, EconomyClients C);

    /// <summary>Nova raids Bob; Cara is a bystander. Everyone has silver.</summary>
    private static async Task<Trio> Players(ServerFixture? fixture = null)
    {
        fixture ??= RaidServer().WithSetting("Economy:SendCooldownSeconds", "0");
        var nova = await fixture.RegisterAndLogin("nova", playerName: "Nova");
        var bob = await fixture.RegisterAndLogin("bob", playerName: "Bob");
        var cara = await fixture.RegisterAndLogin("cara", playerName: "Cara");
        foreach (var p in new[] { nova, bob, cara }) await Fund(fixture, p.Player.PlayerId, 1000);
        return new Trio(fixture, nova, bob, cara, Clients(fixture, nova.SessionToken), Clients(fixture, bob.SessionToken), Clients(fixture, cara.SessionToken));
    }

    private static async Task<long> SeedRaid(ServerFixture fixture, long attacker, long target, RaidStateDb state, RaidController controller = RaidController.ControllerAi)
    {
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RimlinkDbContext>();
        var raid = new Raid
        {
            AttackerPlayerId = attacker, TargetPlayerId = target, State = state, Controller = (int)controller,
            Pawns = Pawns(("Colonist", "Raider")).ToByteArray(), CreatedAt = DateTime.UtcNow, ExpiresAt = DateTime.UtcNow.AddDays(7),
        };
        db.Raids.Add(raid);
        await db.SaveChangesAsync();
        return raid.Id;
    }

    private static async Task SeedLive(ServerFixture fixture, long host, long guest, LiveStateDb state)
    {
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RimlinkDbContext>();
        db.LiveSessions.Add(new LiveSession { Kind = (int)LiveKind.LiveRaid, HostPlayerId = host, GuestPlayerId = guest, State = state, CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
    }

    private static async Task SetRaidState(ServerFixture fixture, long raidId, RaidStateDb state)
    {
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RimlinkDbContext>();
        db.Raids.Single(r => r.Id == raidId).State = state;
        await db.SaveChangesAsync();
    }

    private static Task Deposit(EconomyClients c) =>
        c.Wallet.DepositAsync(new DepositRequest { ClientRequestId = Guid.NewGuid().ToString("N"), Silver = 10, GameTick = 100 }, c.Auth).ResponseAsync;

    private static Task Post(EconomyClients c) =>
        c.Market.PostAsync(new PostRequest { ClientRequestId = Guid.NewGuid().ToString("N"), Items = Items(("Steel", 5)), Price = 10, GameTick = 100 }, c.Auth).ResponseAsync;

    private static Task SendItems(EconomyClients c, long to, bool fromCaravan = false)
    {
        var request = Send(to, items: Items(("Steel", 5)));
        request.FromCaravan = fromCaravan;
        return c.Transfer.SendAsync(request, c.Auth).ResponseAsync;
    }

    private static Task SendSilver(EconomyClients c, long to) => c.Transfer.SendAsync(Send(to, silver: 5), c.Auth).ResponseAsync;

    private static async Task AssertUnderThreat(Func<Task> call)
    {
        var ex = await Assert.ThrowsAsync<RpcException>(call);
        Assert.Equal(ErrorCode.UnderThreat, ex.ErrorDetail()!.Code);
        Assert.Equal(EconomyGuard.UnderThreatMessage, ex.Status.Detail);
    }

    private static async Task AssertEveryOutflowRefused(Trio p)
    {
        long nova = p.Nova.Player.PlayerId;
        await AssertUnderThreat(() => Deposit(p.B));
        await AssertUnderThreat(() => Post(p.B));
        await AssertUnderThreat(() => SendItems(p.B, nova));
        await AssertUnderThreat(() => SendSilver(p.B, nova));
    }

    [Fact]
    public async Task DepositPostAndSendAreRefusedWhileARaidIsQueued()
    {
        var p = await Players();
        using var fixture = p.Fixture;
        await SeedRaid(fixture, p.Nova.Player.PlayerId, p.Bob.Player.PlayerId, RaidStateDb.Queued);

        await AssertEveryOutflowRefused(p);
    }

    [Theory]
    [InlineData(RaidStateDb.Announced)]
    [InlineData(RaidStateDb.Landed)]
    public async Task AnnouncedAndLandedRaidsBlockToo(RaidStateDb state)
    {
        var p = await Players();
        using var fixture = p.Fixture;
        await SeedRaid(fixture, p.Nova.Player.PlayerId, p.Bob.Player.PlayerId, state);

        await AssertEveryOutflowRefused(p);
    }

    [Fact]
    public async Task ALiveRaidBlocksItsHostOnceAccepted()
    {
        var p = await Players();
        using var fixture = p.Fixture;
        long nova = p.Nova.Player.PlayerId, bob = p.Bob.Player.PlayerId;
        // A proposal already has its Queued live raid row; nothing is blocked before the host accepts.
        await SeedRaid(fixture, nova, bob, RaidStateDb.Queued, RaidController.ControllerLive);
        await SeedLive(fixture, bob, nova, LiveStateDb.Proposed);
        await Deposit(p.B);

        await SeedLive(fixture, bob, nova, LiveStateDb.Running);
        await AssertUnderThreat(() => Deposit(p.B));
    }

    [Fact]
    public async Task NothingBlocksTheAttackerOthersOrACaravanHandOver()
    {
        var p = await Players();
        using var fixture = p.Fixture;
        long nova = p.Nova.Player.PlayerId, bob = p.Bob.Player.PlayerId;
        await SeedRaid(fixture, nova, bob, RaidStateDb.Queued);

        await Deposit(p.N);
        await Post(p.N);
        await Deposit(p.C);
        await SendSilver(p.C, bob);
        await SendItems(p.B, nova, fromCaravan: true);
    }

    [Theory]
    [InlineData(RaidStateDb.Resolved)]
    [InlineData(RaidStateDb.Expired)]
    [InlineData(RaidStateDb.Cancelled)]
    public async Task TheBlockEndsWithTheRaid(RaidStateDb final)
    {
        var p = await Players();
        using var fixture = p.Fixture;
        long raid = await SeedRaid(fixture, p.Nova.Player.PlayerId, p.Bob.Player.PlayerId, RaidStateDb.Landed);
        await AssertUnderThreat(() => Deposit(p.B));

        await SetRaidState(fixture, raid, final);

        await Deposit(p.B);
    }

    [Fact]
    public async Task TheBlockCanBeSwitchedOff()
    {
        var p = await Players(RaidServer().WithSetting("Economy:SendCooldownSeconds", "0").WithSetting("Economy:BlockOutflowUnderThreat", "false"));
        using var fixture = p.Fixture;
        await SeedRaid(fixture, p.Nova.Player.PlayerId, p.Bob.Player.PlayerId, RaidStateDb.Queued);

        await Deposit(p.B);
    }

    [Fact]
    public async Task AReplayOfAnAppliedCallStillSucceedsUnderThreat()
    {
        // The outbox replays a call whose answer was lost; refusing it now would return things that already left (a dupe).
        var p = await Players();
        using var fixture = p.Fixture;
        long nova = p.Nova.Player.PlayerId;
        var deposit = new DepositRequest { ClientRequestId = "dep-replay", Silver = 10, GameTick = 100 };
        var post = new PostRequest { ClientRequestId = "post-replay", Items = Items(("Steel", 5)), Price = 10, GameTick = 100 };
        var send = Send(nova, items: Items(("Steel", 5)), requestId: "send-replay");
        await p.B.Wallet.DepositAsync(deposit, p.B.Auth);
        var lot = await p.B.Market.PostAsync(post, p.B.Auth);
        var parcel = await p.B.Transfer.SendAsync(send, p.B.Auth);
        await SeedRaid(fixture, nova, p.Bob.Player.PlayerId, RaidStateDb.Queued);

        Assert.Equal(1010, (await p.B.Wallet.DepositAsync(deposit, p.B.Auth)).Balance);
        Assert.Equal(lot.Id, (await p.B.Market.PostAsync(post, p.B.Auth)).Id);
        Assert.Equal(parcel.Id, (await p.B.Transfer.SendAsync(send, p.B.Auth)).Id);
    }

    [Fact]
    public async Task InboundStaysOpenUnderThreat()
    {
        var p = await Players();
        using var fixture = p.Fixture;
        long nova = p.Nova.Player.PlayerId, bob = p.Bob.Player.PlayerId;
        await SocialFixtures.AcceptGifts(fixture, bob); // a gift waits for a decision otherwise
        var novasLot = await p.N.Market.PostAsync(new PostRequest { ClientRequestId = "n-lot", Items = Items(("Steel", 5)), Price = 10, GameTick = 100 }, p.N.Auth);
        var bobsLot = await p.B.Market.PostAsync(new PostRequest { ClientRequestId = "b-lot", Items = Items(("Steel", 5)), Price = 10, GameTick = 100 }, p.B.Auth);
        await SendItems(p.N, bob);
        var gift = await p.B.Mailbox.ListAsync(new Empty(), p.B.Auth);
        await SeedRaid(fixture, nova, bob, RaidStateDb.Queued);

        await p.B.Wallet.WithdrawAsync(new WithdrawRequest { ClientRequestId = "wd", Silver = 5 }, p.B.Auth);
        await p.B.Market.BuyAsync(new LotRef { LotId = novasLot.Id }, p.B.Auth);
        await p.B.Market.WithdrawAsync(new LotRef { LotId = bobsLot.Id }, p.B.Auth);
        await p.B.Mailbox.DeliverAsync(new DeliverRequest { ParcelId = Assert.Single(gift.Parcels).Id, GameTick = 200 }, p.B.Auth);
    }
}
