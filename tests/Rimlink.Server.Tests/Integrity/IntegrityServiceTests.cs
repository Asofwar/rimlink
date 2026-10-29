using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Feed;
using Rimlink.Server.Storage;
using Rimlink.Server.Storage.Entities;
using Rimlink.Server.Tests.Support;
using Xunit;
using static Rimlink.Server.Tests.Support.EconomyFixtures;
using static Rimlink.Server.Tests.Support.SaveFixtures;
using Parcel = Rimlink.Server.Storage.Entities.Parcel;

namespace Rimlink.Server.Tests.Integrity;

public class IntegrityServiceTests
{
    private static ServerFixture Server() => EconomyServer().WithSetting("Auth:AdminLogins:0", "admin");

    private static async Task Upload(ServerFixture fixture, LoginReply player, long tick, long wealth, bool devMode = false)
    {
        var saves = new Protocol.V1.Saves.SavesClient(fixture.CreateChannel());
        byte[] save = MakeSave(ServerFixture.WorldSeed, player.Player.Name, tick);
        var header = HeaderFor(save, ServerFixture.WorldSeed, player.Player.Name, tick);
        header.WealthTotal = wealth;
        header.DevMode = devMode;
        await SaveFixtures.Upload(saves, player.SessionToken, save, header);
    }

    private static async Task<T> Db<T>(ServerFixture fixture, Func<RimlinkDbContext, Task<T>> query)
    {
        using var scope = fixture.Services.CreateScope();
        return await query(scope.ServiceProvider.GetRequiredService<RimlinkDbContext>());
    }

    private static Task<Player> PlayerRow(ServerFixture fixture, LoginReply player) =>
        Db(fixture, db => db.Players.AsNoTracking().SingleAsync(p => p.Id == player.Player.PlayerId));

    private static Task<List<string>> Audit(ServerFixture fixture) =>
        Db(fixture, db => db.AdminActions.AsNoTracking().OrderBy(a => a.Id).Select(a => a.Action + " " + a.ActorName).ToListAsync());

    private static async Task Delivered(ServerFixture fixture, LoginReply to, long tick, ItemList? items = null, PawnList? pawns = null, long silver = 0)
    {
        await Db(fixture, async db =>
        {
            db.Parcels.Add(new Parcel
            {
                ToPlayerId = to.Player.PlayerId, Kind = ParcelKindDb.Purchase, Items = (items ?? new ItemList()).ToByteArray(),
                Pawns = (pawns ?? new PawnList()).ToByteArray(), Silver = silver, CreatedAt = DateTime.UtcNow, DeliveredAt = DateTime.UtcNow, DeliveredTick = tick,
            });
            return await db.SaveChangesAsync();
        });
    }

    [Fact]
    public async Task DevModeSaveFreezesAPlayer()
    {
        using var fixture = Server();
        using var probe = fixture.Probe();
        var nova = await fixture.RegisterAndLogin("nova", playerName: "Nova");

        await Upload(fixture, nova, tick: 1000, wealth: 30000, devMode: true);

        var alert = FeedProbe.Data(await probe.Next(FeedTypes.IntegrityAlert));
        var row = await PlayerRow(fixture, nova);
        Assert.Equal("devmode", alert.GetProperty("kind").GetString());
        Assert.True(alert.GetProperty("frozen").GetBoolean());
        Assert.Equal("Nova", alert.GetProperty("player").GetProperty("name").GetString());
        Assert.NotNull(row.EconomyFrozenAt);
        Assert.Equal("dev mode was on in save v1", row.EconomyFreezeReason);
        Assert.Contains("integrity-devmode system", await Audit(fixture));
    }

    [Fact]
    public async Task DevModeSaveOfAnAdminIsOnlyRecorded()
    {
        using var fixture = Server();
        using var probe = fixture.Probe();
        var admin = await fixture.RegisterAndLogin("admin", playerName: "Boss");

        await Upload(fixture, admin, tick: 1000, wealth: 30000, devMode: true);

        Assert.Null((await PlayerRow(fixture, admin)).EconomyFrozenAt);
        Assert.True(await Db(fixture, db => db.SaveVersions.AnyAsync(v => v.PlayerId == admin.Player.PlayerId && v.DevMode)));
        Assert.DoesNotContain(await probe.Drain(TimeSpan.FromMilliseconds(500)), e => e.Type == FeedTypes.IntegrityAlert);
    }

    [Fact]
    public async Task DevModeEveryoneRecordsOnly()
    {
        using var fixture = Server().WithSetting("Integrity:DevMode", "Everyone");
        var nova = await fixture.RegisterAndLogin("nova", playerName: "Nova");

        await Upload(fixture, nova, tick: 1000, wealth: 30000, devMode: true);

        Assert.Null((await PlayerRow(fixture, nova)).EconomyFrozenAt);
    }

    [Fact]
    public async Task WealthJumpWarns()
    {
        using var fixture = Server();
        using var probe = fixture.Probe();
        var nova = await fixture.RegisterAndLogin("nova", playerName: "Nova");
        await Upload(fixture, nova, tick: 1000, wealth: 50000);

        await Upload(fixture, nova, tick: 61000, wealth: 80000);

        var alert = FeedProbe.Data(await probe.Next(FeedTypes.IntegrityAlert));
        Assert.Equal("wealth-warn", alert.GetProperty("kind").GetString());
        Assert.Equal(30000, alert.GetProperty("rate").GetInt64());
        Assert.Equal(2, alert.GetProperty("version").GetInt32());
        Assert.False(alert.GetProperty("frozen").GetBoolean());
        Assert.Null((await PlayerRow(fixture, nova)).EconomyFrozenAt);
        Assert.Contains("integrity-wealth system", await Audit(fixture));
    }

    [Fact]
    public async Task WealthJumpFreezes()
    {
        using var fixture = Server();
        using var probe = fixture.Probe();
        var nova = await fixture.RegisterAndLogin("nova", playerName: "Nova");
        await Upload(fixture, nova, tick: 1000, wealth: 50000);

        await Upload(fixture, nova, tick: 61000, wealth: 200000);

        var alert = FeedProbe.Data(await probe.Next(FeedTypes.IntegrityAlert));
        Assert.Equal("wealth-freeze", alert.GetProperty("kind").GetString());
        Assert.True(alert.GetProperty("frozen").GetBoolean());
        Assert.Equal("wealth +150,000/day in save v2", (await PlayerRow(fixture, nova)).EconomyFreezeReason);
    }

    [Fact]
    public async Task DeliveredPurchaseBetweenSavesDoesNotAlert()
    {
        using var fixture = Server();
        using var probe = fixture.Probe();
        var nova = await fixture.RegisterAndLogin("nova", playerName: "Nova");
        await Upload(fixture, nova, tick: 1000, wealth: 50000);
        await Delivered(fixture, nova, tick: 30000, items: new ItemList { Items = { Item("Gold", 1, marketValueX100: 4_000_000) } });

        await Upload(fixture, nova, tick: 31000, wealth: 85000);

        Assert.DoesNotContain(await probe.Drain(TimeSpan.FromMilliseconds(500)), e => e.Type == FeedTypes.IntegrityAlert);
    }

    [Fact]
    public async Task PawnValueAndSilverCountAsImported()
    {
        using var fixture = Server();
        using var probe = fixture.Probe();
        var nova = await fixture.RegisterAndLogin("nova", playerName: "Nova");
        await Upload(fixture, nova, tick: 1000, wealth: 50000);
        var pawn = Pawn("Colonist", "Ann");
        pawn.MarketValueX100 = 10_000_000;
        await Delivered(fixture, nova, tick: 30000, pawns: new PawnList { Pawns = { pawn } }, silver: 5000);
        await Delivered(fixture, nova, tick: 90000, silver: 100000); // after the new save: not imported yet

        await Upload(fixture, nova, tick: 61000, wealth: 176000);

        // 176000 − 50000 − (100000 pawn + 5000 silver) = 21000: a warning; without the pawn it would freeze, with the late parcel be quiet.
        var alert = FeedProbe.Data(await probe.Next(FeedTypes.IntegrityAlert));
        Assert.Equal(21000, alert.GetProperty("net").GetInt64());
        Assert.Equal("wealth-warn", alert.GetProperty("kind").GetString());
    }

    [Fact]
    public async Task FrozenPlayerGetsNoNewAlerts()
    {
        using var fixture = Server();
        var nova = await fixture.RegisterAndLogin("nova", playerName: "Nova");
        await Upload(fixture, nova, tick: 1000, wealth: 50000);
        using (var scope = fixture.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<Rimlink.Server.Application.Economy.PlayerFreeze>().Set(nova.Player.PlayerId, true, "manual");
        using var probe = fixture.Probe();

        await Upload(fixture, nova, tick: 61000, wealth: 500000, devMode: true);
        await Upload(fixture, nova, tick: 121000, wealth: 900000);

        Assert.DoesNotContain(await probe.Drain(TimeSpan.FromMilliseconds(500)), e => e.Type == FeedTypes.IntegrityAlert);
        Assert.Empty((await Audit(fixture)).Where(a => a.StartsWith("integrity-")));
        Assert.Equal("manual", (await PlayerRow(fixture, nova)).EconomyFreezeReason);
    }

    [Fact]
    public async Task FirstSaveIsNotChecked()
    {
        using var fixture = Server();
        using var probe = fixture.Probe();
        var nova = await fixture.RegisterAndLogin("nova", playerName: "Nova");

        await Upload(fixture, nova, tick: 1000, wealth: 10_000_000);

        Assert.DoesNotContain(await probe.Drain(TimeSpan.FromMilliseconds(500)), e => e.Type == FeedTypes.IntegrityAlert);
    }
}
