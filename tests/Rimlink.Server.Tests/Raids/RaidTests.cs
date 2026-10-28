using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Rimlink.Protocol.V1;
using Rimlink.Server.Storage;
using Rimlink.Server.Tests.Support;
using Xunit;
using static Rimlink.Server.Tests.Support.EconomyFixtures;
using static Rimlink.Server.Tests.Support.GrpcTestExtensions;
using static Rimlink.Server.Tests.Support.RaidFixtures;

namespace Rimlink.Server.Tests.Raids;

public class RaidTests
{
    private sealed record Pair(ServerFixture Fixture, LoginReply Nova, LoginReply Bob, RaidClients N, RaidClients B);

    /// <summary>Nova (attacker) and Bob (raidable target) on a raid server.</summary>
    private static async Task<Pair> NovaAndBob(ServerFixture? fixture = null)
    {
        fixture ??= RaidServer();
        var nova = await fixture.RegisterAndLogin("nova", playerName: "Nova");
        var bob = await fixture.RegisterAndLogin("bob", playerName: "Bob");
        await MakeTargetRaidable(fixture, bob.Player.PlayerId);
        return new Pair(fixture, nova, bob, RaidClientsOf(fixture, nova.SessionToken), RaidClientsOf(fixture, bob.SessionToken));
    }

    private static async Task<long> OutgoingTick(ServerFixture fixture, long playerId)
    {
        using var scope = fixture.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<RimlinkDbContext>().Players.SingleAsync(p => p.Id == playerId)).LastOutgoingTick;
    }

    [Fact]
    public async Task LaunchQueuesARaidTheTargetSeesAndAnEvent()
    {
        var p = await NovaAndBob();
        using var fixture = p.Fixture;
        var events = new Protocol.V1.Events.EventsClient(fixture.CreateChannel());
        using var bobStream = events.Subscribe(new Empty(), p.B.Auth);

        var raid = await p.N.Raids.LaunchAsync(Launch(p.Bob.Player.PlayerId, tick: 500), p.N.Auth);

        Assert.True(raid.Id > 0);
        Assert.Equal(RaidState.RaidQueued, raid.State);
        Assert.Equal(5000, raid.LootCap); // min(100000 * 10 %, 5000)
        var pending = await p.B.Raids.PendingAsync(new Empty(), p.B.Auth);
        var seen = Assert.Single(pending.Raids);
        Assert.Equal(raid.Id, seen.Id);
        Assert.Equal("Nova", seen.Attacker.Name);
        Assert.Equal(2, seen.Pawns.Pawns.Count);
        Assert.All(seen.Pawns.Pawns, b => Assert.Empty(b.ScribeXmlZstd));
        Assert.Equal(raid.Id, await ReadEvent(bobStream, ServerEvent.BodyOneofCase.RaidQueued, e => e.RaidQueued.Raid.Id));
        Assert.Equal(500, await OutgoingTick(fixture, p.Nova.Player.PlayerId));
    }

    [Fact]
    public async Task SnapshotLaunchDoesNotStampTheAttacker()
    {
        var p = await NovaAndBob();
        using var fixture = p.Fixture;

        var raid = await p.N.Raids.LaunchAsync(Launch(p.Bob.Player.PlayerId, mode: PawnMode.PawnSnapshot, tick: 500), p.N.Auth);

        Assert.Equal(2500, raid.LootCap);
        Assert.Equal(0, await OutgoingTick(fixture, p.Nova.Player.PlayerId));
    }

    [Fact]
    public async Task InstantSnapshotLootCapIsAQuarter()
    {
        var p = await NovaAndBob();
        using var fixture = p.Fixture;

        var raid = await p.N.Raids.LaunchAsync(Launch(p.Bob.Player.PlayerId, mode: PawnMode.PawnSnapshot, timing: RaidTiming.TimingInstant), p.N.Auth);

        Assert.Equal(1250, raid.LootCap);
    }

    [Fact]
    public async Task SameClientRequestIdReturnsTheSameRaid()
    {
        var p = await NovaAndBob();
        using var fixture = p.Fixture;

        var first = await p.N.Raids.LaunchAsync(Launch(p.Bob.Player.PlayerId, requestId: "r1"), p.N.Auth);
        var second = await p.N.Raids.LaunchAsync(Launch(p.Bob.Player.PlayerId, requestId: "r1"), p.N.Auth);

        Assert.Equal(first.Id, second.Id);
        Assert.Single((await p.B.Raids.PendingAsync(new Empty(), p.B.Auth)).Raids);
    }

    [Theory]
    [InlineData(11)]
    public async Task TooManyRaidersIsParcelTooBig(int pawns)
    {
        var p = await NovaAndBob();
        using var fixture = p.Fixture;

        var ex = await Assert.ThrowsAsync<RpcException>(() => p.N.Raids.LaunchAsync(Launch(p.Bob.Player.PlayerId, pawns: pawns), p.N.Auth).ResponseAsync);

        Assert.Equal(ErrorCode.ParcelTooBig, ex.ErrorDetail()!.Code);
    }

    [Fact]
    public async Task LaunchingAtYourselfIsSelfTrade()
    {
        var p = await NovaAndBob();
        using var fixture = p.Fixture;

        var ex = await Assert.ThrowsAsync<RpcException>(() => p.N.Raids.LaunchAsync(Launch(p.Nova.Player.PlayerId), p.N.Auth).ResponseAsync);

        Assert.Equal(ErrorCode.SelfTrade, ex.ErrorDetail()!.Code);
    }

    [Fact]
    public async Task SecondActiveRaidIsRaidActive()
    {
        var p = await NovaAndBob();
        using var fixture = p.Fixture;
        var alice = await fixture.RegisterAndLogin("alice");
        await MakeTargetRaidable(fixture, alice.Player.PlayerId);
        await p.N.Raids.LaunchAsync(Launch(p.Bob.Player.PlayerId), p.N.Auth);

        var ex = await Assert.ThrowsAsync<RpcException>(() => p.N.Raids.LaunchAsync(Launch(alice.Player.PlayerId), p.N.Auth).ResponseAsync);

        Assert.Equal(ErrorCode.RaidActive, ex.ErrorDetail()!.Code);
    }

    [Fact]
    public async Task TargetRaidedWithinEightHoursIsRaidCooldown()
    {
        var p = await NovaAndBob();
        using var fixture = p.Fixture;
        var alice = await fixture.RegisterAndLogin("alice");
        var a = RaidClientsOf(fixture, alice.SessionToken);
        await p.N.Raids.LaunchAsync(Launch(p.Bob.Player.PlayerId), p.N.Auth);

        var ex = await Assert.ThrowsAsync<RpcException>(() => a.Raids.LaunchAsync(Launch(p.Bob.Player.PlayerId), a.Auth).ResponseAsync);

        Assert.Equal(ErrorCode.RaidCooldown, ex.ErrorDetail()!.Code);
    }

    [Fact]
    public async Task NewTargetIsTargetProtected()
    {
        using var fixture = EconomyServer(); // spec defaults: 3 days, 20000 wealth
        var nova = await fixture.RegisterAndLogin("nova");
        var bob = await fixture.RegisterAndLogin("bob");
        var n = RaidClientsOf(fixture, nova.SessionToken);
        using (var scope = fixture.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<RimlinkDbContext>().Players.Where(x => x.Id == bob.Player.PlayerId)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.FirstSaveAt, DateTime.UtcNow).SetProperty(x => x.WealthTotal, 100000L));

        var young = await Assert.ThrowsAsync<RpcException>(() => n.Raids.LaunchAsync(Launch(bob.Player.PlayerId), n.Auth).ResponseAsync);
        Assert.Equal(ErrorCode.TargetProtected, young.ErrorDetail()!.Code);

        await MakeTargetRaidable(fixture, bob.Player.PlayerId, wealthItems: 100, wealthTotal: 100);
        var poor = await Assert.ThrowsAsync<RpcException>(() => n.Raids.LaunchAsync(Launch(bob.Player.PlayerId), n.Auth).ResponseAsync);
        Assert.Equal(ErrorCode.TargetProtected, poor.ErrorDetail()!.Code);
    }

    [Fact]
    public async Task PeacefulPlayerCannotRaidOrBeRaided()
    {
        var p = await NovaAndBob();
        using var fixture = p.Fixture;
        await MakeTargetRaidable(fixture, p.Nova.Player.PlayerId);

        var flags = await p.B.Raids.SetPeacefulAsync(new PeacefulRequest { Peaceful = true }, p.B.Auth);
        Assert.True(flags.Peaceful);
        var asTarget = await Assert.ThrowsAsync<RpcException>(() => p.N.Raids.LaunchAsync(Launch(p.Bob.Player.PlayerId), p.N.Auth).ResponseAsync);
        Assert.Equal(ErrorCode.PeacefulMode, asTarget.ErrorDetail()!.Code);
        var asAttacker = await Assert.ThrowsAsync<RpcException>(() => p.B.Raids.LaunchAsync(Launch(p.Nova.Player.PlayerId), p.B.Auth).ResponseAsync);
        Assert.Equal(ErrorCode.PeacefulMode, asAttacker.ErrorDetail()!.Code);

        var off = await p.B.Raids.SetPeacefulAsync(new PeacefulRequest { Peaceful = false }, p.B.Auth);
        Assert.True(off.Peaceful);
        Assert.NotNull(off.PeacefulOffAt);
        Assert.True(off.PeacefulOffAt.ToDateTime() > DateTime.UtcNow + TimeSpan.FromHours(23));
        var still = await Assert.ThrowsAsync<RpcException>(() => p.N.Raids.LaunchAsync(Launch(p.Bob.Player.PlayerId), p.N.Auth).ResponseAsync);
        Assert.Equal(ErrorCode.PeacefulMode, still.ErrorDetail()!.Code);
        Assert.True((await p.B.Raids.FlagsAsync(new Empty(), p.B.Auth)).Peaceful);
    }

    [Fact]
    public async Task AnnounceSetsLandAtTickAndIsIdempotent()
    {
        var p = await NovaAndBob();
        using var fixture = p.Fixture;
        var warned = await p.N.Raids.LaunchAsync(Launch(p.Bob.Player.PlayerId), p.N.Auth);

        var announced = await p.B.Raids.AnnounceAsync(new RaidTick { RaidId = warned.Id, GameTick = 1000 }, p.B.Auth);
        var again = await p.B.Raids.AnnounceAsync(new RaidTick { RaidId = warned.Id, GameTick = 2000 }, p.B.Auth);

        Assert.Equal(RaidState.RaidAnnounced, announced.State);
        Assert.Equal(1000, announced.AnnouncedTick);
        Assert.Equal(61000, announced.LandAtTick);
        Assert.Equal(61000, again.LandAtTick);

        // Instant: lands at the announcement tick (bob is on cooldown, so alice raids nova).
        var alice = await fixture.RegisterAndLogin("alice");
        await MakeTargetRaidable(fixture, p.Nova.Player.PlayerId);
        var a = RaidClientsOf(fixture, alice.SessionToken);
        var instantAtNova = await a.Raids.LaunchAsync(Launch(p.Nova.Player.PlayerId, timing: RaidTiming.TimingInstant), a.Auth);
        var novaSees = await p.N.Raids.AnnounceAsync(new RaidTick { RaidId = instantAtNova.Id, GameTick = 700 }, p.N.Auth);
        Assert.Equal(700, novaSees.LandAtTick);
    }

    [Fact]
    public async Task LandStampsTheTargetAndNotifiesTheAttacker()
    {
        var p = await NovaAndBob();
        using var fixture = p.Fixture;
        var events = new Protocol.V1.Events.EventsClient(fixture.CreateChannel());
        using var novaStream = events.Subscribe(new Empty(), p.N.Auth);
        var raid = await p.N.Raids.LaunchAsync(Launch(p.Bob.Player.PlayerId), p.N.Auth);
        await p.B.Raids.AnnounceAsync(new RaidTick { RaidId = raid.Id, GameTick = 1000 }, p.B.Auth);

        await p.B.Raids.LandAsync(new RaidTick { RaidId = raid.Id, GameTick = 70000 }, p.B.Auth);
        await p.B.Raids.LandAsync(new RaidTick { RaidId = raid.Id, GameTick = 70001 }, p.B.Auth); // idempotent

        var mine = Assert.Single((await p.N.Raids.MineAsync(new Empty(), p.N.Auth)).Raids);
        Assert.Equal(RaidState.RaidLanded, mine.State);
        Assert.Equal(70000, mine.LandedTick);
        Assert.Equal(70000, await OutgoingTick(fixture, p.Bob.Player.PlayerId));
        Assert.Equal(raid.Id, await ReadEvent(novaStream, ServerEvent.BodyOneofCase.RaidLanded, e => e.RaidLanded.RaidId));
        Assert.Empty((await p.B.Raids.PendingAsync(new Empty(), p.B.Auth)).Raids);
    }

    [Fact]
    public async Task GetCarriesBlobsForBothSidesOnly()
    {
        var p = await NovaAndBob();
        using var fixture = p.Fixture;
        var alice = await fixture.RegisterAndLogin("alice");
        var a = RaidClientsOf(fixture, alice.SessionToken);
        var raid = await p.N.Raids.LaunchAsync(Launch(p.Bob.Player.PlayerId), p.N.Auth);

        Assert.Equal(64, (await p.N.Raids.GetAsync(new RaidRef { RaidId = raid.Id }, p.N.Auth)).Pawns.Pawns[0].ScribeXmlZstd.Length);
        Assert.Equal(64, (await p.B.Raids.GetAsync(new RaidRef { RaidId = raid.Id }, p.B.Auth)).Pawns.Pawns[0].ScribeXmlZstd.Length);
        var ex = await Assert.ThrowsAsync<RpcException>(() => a.Raids.GetAsync(new RaidRef { RaidId = raid.Id }, a.Auth).ResponseAsync);
        Assert.Equal(StatusCode.NotFound, ex.StatusCode);
    }

    [Fact]
    public async Task LaunchIsRateLimitedPerPlayer()
    {
        var p = await NovaAndBob(RaidServer().WithRateLimits());
        using var fixture = p.Fixture;
        var alice = await fixture.RegisterAndLogin("alice");
        await MakeTargetRaidable(fixture, alice.Player.PlayerId);
        await p.N.Raids.LaunchAsync(Launch(p.Bob.Player.PlayerId), p.N.Auth);

        var ex = await Assert.ThrowsAsync<RpcException>(() => p.N.Raids.LaunchAsync(Launch(alice.Player.PlayerId), p.N.Auth).ResponseAsync);

        Assert.Equal(StatusCode.ResourceExhausted, ex.StatusCode);
    }
}
