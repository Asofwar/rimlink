using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Admin;
using Rimlink.Server.Application.Raids;
using Rimlink.Server.Storage;
using Rimlink.Server.Tests.Support;
using Xunit;
using Role = Rimlink.Server.Storage.Entities.Role;
using static Rimlink.Server.Tests.Support.EconomyFixtures;
using static Rimlink.Server.Tests.Support.GrpcTestExtensions;
using static Rimlink.Server.Tests.Support.RaidFixtures;
using static Rimlink.Server.Tests.Support.SaveFixtures;

namespace Rimlink.Server.Tests.Raids;

/// <summary>Findings of the final review, each pinned by a test that failed first.</summary>
public class RaidFixPassTests
{
    private sealed record Pair(ServerFixture Fixture, LoginReply Nova, LoginReply Bob, RaidClients N, RaidClients B);

    private static async Task<Pair> NovaAndBob(ServerFixture? fixture = null, long wealthItems = 100000)
    {
        fixture ??= RaidServer();
        var nova = await fixture.RegisterAndLogin("nova", playerName: "Nova");
        var bob = await fixture.RegisterAndLogin("bob", playerName: "Bob");
        await MakeTargetRaidable(fixture, bob.Player.PlayerId, wealthItems: wealthItems);
        return new Pair(fixture, nova, bob, RaidClientsOf(fixture, nova.SessionToken), RaidClientsOf(fixture, bob.SessionToken));
    }

    private static async Task<Raid> Landed(Pair p, int pawns = 2)
    {
        var raid = await p.N.Raids.LaunchAsync(Launch(p.Bob.Player.PlayerId, pawns: pawns), p.N.Auth);
        await p.B.Raids.AnnounceAsync(new RaidTick { RaidId = raid.Id, GameTick = 1000 }, p.B.Auth);
        await p.B.Raids.LandAsync(new RaidTick { RaidId = raid.Id, GameTick = 70000 }, p.B.Auth);
        return raid;
    }

    private static RaidReport AllDead(long raidId, int pawns, params ItemBlob[] loot)
    {
        var report = new RaidReport { RaidId = raidId, GameTick = 80000, Loot = new ItemList(), Captives = new PawnList() };
        for (int i = 0; i < pawns; i++) report.Outcomes.Add(new PawnOutcomeMsg { Index = i, Outcome = PawnOutcome.OutcomeDead });
        report.Loot.Items.AddRange(loot);
        return report;
    }

    [Fact]
    public async Task LootCapCountsHundredthsSoTwoHalfSilverStacksDoNotSlipUnderIt()
    {
        var p = await NovaAndBob(wealthItems: 20); // cap = 2 silver
        using var fixture = p.Fixture;
        var raid = await Landed(p);
        Assert.Equal(2, raid.LootCap);

        var over = AllDead(raid.Id, 2, Item("Steel", 1, marketValueX100: 125), Item("Steel", 1, marketValueX100: 125)); // 2.50 silver: over, not "2"
        var ex = await Assert.ThrowsAsync<RpcException>(() => p.B.Raids.ReportAsync(over, p.B.Auth).ResponseAsync);
        Assert.Equal(ErrorCode.LootOverCap, ex.ErrorDetail()!.Code);

        await p.B.Raids.ReportAsync(AllDead(raid.Id, 2, Item("Steel", 1, marketValueX100: 200)), p.B.Auth); // exactly 2.00
        Assert.Equal(2, Assert.Single((await p.N.Raids.MineAsync(new Empty(), p.N.Auth)).Raids).Summary.LootValue);
    }

    [Fact]
    public async Task LandedRaidWithoutAReportExpiresAndReturnsThePawns()
    {
        var p = await NovaAndBob();
        using var fixture = p.Fixture;
        var raid = await Landed(p);
        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RimlinkDbContext>();
            await db.Raids.Where(r => r.Id == raid.Id).ExecuteUpdateAsync(s => s.SetProperty(r => r.ExpiresAt, DateTime.UtcNow - TimeSpan.FromMinutes(1)));
            Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<RaidService>().ExpireDue(DateTime.UtcNow));
        }

        Assert.Equal(RaidState.RaidExpired, Assert.Single((await p.N.Raids.MineAsync(new Empty(), p.N.Auth)).Raids).State);
        Assert.Equal(ParcelKind.ParcelRaidReturn, Assert.Single((await p.N.Mailbox.ListAsync(new Empty(), p.N.Auth)).Parcels).Kind);
        var late = await Assert.ThrowsAsync<RpcException>(() => p.B.Raids.ReportAsync(AllDead(raid.Id, 2), p.B.Auth).ResponseAsync);
        Assert.Equal(ErrorCode.RaidGone, late.ErrorDetail()!.Code);
    }

    [Fact]
    public async Task LandingResetsTheExpiryClock()
    {
        var p = await NovaAndBob();
        using var fixture = p.Fixture;
        var raid = await p.N.Raids.LaunchAsync(Launch(p.Bob.Player.PlayerId), p.N.Auth);
        await p.B.Raids.AnnounceAsync(new RaidTick { RaidId = raid.Id, GameTick = 1000 }, p.B.Auth);
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RimlinkDbContext>();
        await db.Raids.Where(r => r.Id == raid.Id).ExecuteUpdateAsync(s => s.SetProperty(r => r.ExpiresAt, DateTime.UtcNow + TimeSpan.FromHours(1))); // six days of waiting already spent

        await p.B.Raids.LandAsync(new RaidTick { RaidId = raid.Id, GameTick = 70000 }, p.B.Auth);

        var row = await db.Raids.AsNoTracking().SingleAsync(r => r.Id == raid.Id);
        Assert.True(row.ExpiresAt > DateTime.UtcNow + TimeSpan.FromDays(6), "the defender gets the full expiry window to report");
    }

    [Fact]
    public async Task CancelRereadsTheRaidInsideItsTransaction()
    {
        var p = await NovaAndBob();
        using var fixture = p.Fixture;
        var raid = await Landed(p);
        using var scope = fixture.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<RaidService>();
        var stale = await scope.ServiceProvider.GetRequiredService<RimlinkDbContext>().Raids.FirstAsync(r => r.Id == raid.Id); // tracked as Landed
        await p.B.Raids.ReportAsync(AllDead(raid.Id, 2), p.B.Auth); // resolved meanwhile

        Assert.False(await service.Cancel(raid.Id, "race", returnPawns: true));

        Assert.Equal(RaidState.RaidResolved, Assert.Single((await p.N.Raids.MineAsync(new Empty(), p.N.Auth)).Raids).State);
        Assert.Empty((await p.N.Mailbox.ListAsync(new Empty(), p.N.Auth)).Parcels);
        Assert.NotNull(stale);
    }

    [Fact]
    public async Task GetOfACancelledRaidIsRaidGoneForTheTarget()
    {
        var p = await NovaAndBob(RaidServer().WithSetting("Auth:AdminLogins:0", "admin"));
        using var fixture = p.Fixture;
        var admin = await fixture.RegisterAndLogin("admin");
        var actor = new Actor(await fixture.AccountIdOf("admin"), admin.Player.PlayerId, Role.Admin, "admin");
        var raid = await p.N.Raids.LaunchAsync(Launch(p.Bob.Player.PlayerId), p.N.Auth);
        await p.B.Raids.AnnounceAsync(new RaidTick { RaidId = raid.Id, GameTick = 1000 }, p.B.Auth);
        using (var scope = fixture.Services.CreateScope())
            Assert.True(await scope.ServiceProvider.GetRequiredService<AdminOperations>().CancelRaid(actor, raid.Id, "test"));

        var ex = await Assert.ThrowsAsync<RpcException>(() => p.B.Raids.GetAsync(new RaidRef { RaidId = raid.Id }, p.B.Auth).ResponseAsync);
        Assert.Equal(ErrorCode.RaidGone, ex.ErrorDetail()!.Code);
        Assert.Equal(RaidState.RaidCancelled, (await p.N.Raids.GetAsync(new RaidRef { RaidId = raid.Id }, p.N.Auth)).State); // the attacker may still look at it
    }

    [Fact]
    public async Task AdminCancelOfALandedRaidCanReturnThePawns()
    {
        var p = await NovaAndBob(RaidServer().WithSetting("Auth:AdminLogins:0", "admin"));
        using var fixture = p.Fixture;
        var admin = await fixture.RegisterAndLogin("admin");
        var actor = new Actor(await fixture.AccountIdOf("admin"), admin.Player.PlayerId, Role.Admin, "admin");
        var raid = await Landed(p);

        using (var scope = fixture.Services.CreateScope())
            Assert.True(await scope.ServiceProvider.GetRequiredService<AdminOperations>().CancelRaid(actor, raid.Id, "stuck", returnPawns: true));

        Assert.Equal(ParcelKind.ParcelRaidReturn, Assert.Single((await p.N.Mailbox.ListAsync(new Empty(), p.N.Auth)).Parcels).Kind);
    }

    [Fact]
    public async Task CancelOfAResolvedRaidChangesNothing()
    {
        var p = await NovaAndBob(RaidServer().WithSetting("Auth:AdminLogins:0", "admin"));
        using var fixture = p.Fixture;
        var admin = await fixture.RegisterAndLogin("admin");
        var actor = new Actor(await fixture.AccountIdOf("admin"), admin.Player.PlayerId, Role.Admin, "admin");
        var raid = await Landed(p);
        await p.B.Raids.ReportAsync(AllDead(raid.Id, 2), p.B.Auth);

        using (var scope = fixture.Services.CreateScope())
            Assert.False(await scope.ServiceProvider.GetRequiredService<AdminOperations>().CancelRaid(actor, raid.Id, "late", returnPawns: true));

        Assert.Equal(RaidState.RaidResolved, Assert.Single((await p.N.Raids.MineAsync(new Empty(), p.N.Auth)).Raids).State);
        Assert.Empty((await p.N.Mailbox.ListAsync(new Empty(), p.N.Auth)).Parcels);
    }

    [Fact]
    public async Task AVisitTicketExpiresWhenTheVisitorNeverEndsIt()
    {
        using var fixture = EconomyServer().WithSetting("Raids:VisitHours", "6");
        var nova = await fixture.RegisterAndLogin("nova");
        var bob = await fixture.RegisterAndLogin("bob", playerName: "Bob");
        var b = RaidClientsOf(fixture, bob.SessionToken);
        byte[] save = MakeSave(ServerFixture.WorldSeed, "Bob", tick: 10);
        await Upload(b.Saves, bob.SessionToken, save, HeaderFor(save, ServerFixture.WorldSeed, "Bob", tick: 10));
        var n = RaidClientsOf(fixture, nova.SessionToken);
        var ticket = await n.Visits.BeginAsync(new VisitRequest { TargetPlayerId = bob.Player.PlayerId, GameTick = 5 }, n.Auth);
        Assert.NotEmpty(await Visits.VisitTests.DownloadOf(n, bob.Player.PlayerId));

        // The game crashed mid-visit and nobody sent End: seven hours later the ticket is no longer good.
        using (var scope = fixture.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<RimlinkDbContext>().Visits.Where(v => v.Id == ticket.VisitId)
                .ExecuteUpdateAsync(s => s.SetProperty(v => v.StartedAt, DateTime.UtcNow - TimeSpan.FromHours(7)));

        var ex = await Assert.ThrowsAsync<RpcException>(() => Visits.VisitTests.DownloadOf(n, bob.Player.PlayerId));
        Assert.Equal(StatusCode.PermissionDenied, ex.StatusCode);
    }
}
