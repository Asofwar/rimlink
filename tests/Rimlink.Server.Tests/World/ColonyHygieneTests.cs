using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Rimlink.Server.Application.Sessions;
using Rimlink.Server.Application.World;
using Rimlink.Server.Tests.Support;
using Xunit;
using static Rimlink.Server.Tests.Support.HygieneFixtures;

namespace Rimlink.Server.Tests.World;

/// <summary>The sweep archives a colony only when every enabled condition holds (spec §2.3).</summary>
public class ColonyHygieneTests
{
    private static readonly DateTime Now = DateTime.UtcNow;

    private static async Task<int> Sweep(ServerFixture fixture)
    {
        using var scope = fixture.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ColonyHygiene>().SweepDue(Now);
    }

    /// <summary>A colony whose owner went offline <paramref name="idleDays"/> ago (no login since).</summary>
    private static async Task<Colony> Idle(ServerFixture fixture, string login, int tile, int idleDays, long wealth = 50000, long tick = 30 * GameDay, bool upload = true)
    {
        var colony = upload
            ? await Settled(fixture, login, tile, wealth, tick)
            : await SettledWithoutSave(fixture, login, tile);
        await Offline(fixture, colony.PlayerId);
        await SetActivity(fixture, colony.PlayerId, Now.AddDays(-idleDays - 1), Now.AddDays(-idleDays));
        return colony;
    }

    private static async Task<Colony> SettledWithoutSave(ServerFixture fixture, string login, int tile)
    {
        var reply = await fixture.RegisterAndLogin(login, playerName: NameOf(login));
        var settlement = await new Protocol.V1.World.WorldClient(fixture.CreateChannel())
            .SettleAsync(new Protocol.V1.SettleRequest { Tile = tile, Name = $"{NameOf(login)} Home" }, GrpcTestExtensions.Bearer(reply.SessionToken));
        return new Colony(reply, reply.Player.PlayerId, settlement, []);
    }

    /// <summary>Ends the session and waits until its end is recorded, so a later <see cref="SetActivity"/> is not overwritten.</summary>
    private static async Task Offline(ServerFixture fixture, long playerId)
    {
        var sessions = fixture.Services.GetRequiredService<SessionRegistry>();
        sessions.End(sessions.ByPlayer(playerId)!.Token);
        await Eventually(async () => await Db(fixture, db => db.Players.Where(p => p.Id == playerId).Select(p => p.Account.LastSessionEndAt).SingleAsync()) != null);
    }

    private static Task<bool> IsArchived(ServerFixture fixture, long playerId) =>
        Db(fixture, db => db.ArchivedColonies.AnyAsync(c => c.PlayerId == playerId));

    [Fact]
    public async Task ArchivesAnAbandonedPoorYoungColony()
    {
        using var fixture = HygieneServer();
        var nova = await Idle(fixture, "nova", tile: 100, idleDays: 15);

        Assert.Equal(1, await Sweep(fixture));

        var row = await Db(fixture, db => db.ArchivedColonies.SingleAsync(c => c.PlayerId == nova.PlayerId));
        Assert.Equal(("auto", "system:hygiene", 30), (row.Reason, row.ArchivedBy, row.ColonyGameDays));
    }

    [Theory]
    [InlineData(13, 50000, 30)]   // recently active
    [InlineData(15, 100000, 30)]  // not poor
    [InlineData(15, 50000, 60)]   // a game year old
    public async Task EachConditionProtects(int idleDays, long wealth, int gameDays)
    {
        using var fixture = HygieneServer();
        var nova = await Idle(fixture, "nova", tile: 100, idleDays, wealth, gameDays * GameDay);

        Assert.Equal(0, await Sweep(fixture));
        Assert.False(await IsArchived(fixture, nova.PlayerId));
    }

    [Theory]
    [InlineData("Hygiene:ArchiveMaxWealth", 900000, 30)]
    [InlineData("Hygiene:ArchiveMaxColonyDays", 50000, 200)]
    public async Task DisabledConditionIsIgnored(string key, long wealth, int gameDays)
    {
        using var fixture = HygieneServer().WithSetting(key, "0");
        await Idle(fixture, "nova", tile: 100, idleDays: 15, wealth, gameDays * GameDay);

        Assert.Equal(1, await Sweep(fixture));
    }

    [Fact]
    public async Task SweepOffWhenArchiveAfterDaysIsZero()
    {
        using var fixture = HygieneServer().WithSetting("Hygiene:ArchiveAfterDays", "0");
        await Idle(fixture, "nova", tile: 100, idleDays: 400);

        Assert.Equal(0, await Sweep(fixture));
    }

    [Fact]
    public async Task OnlinePlayerIsSkipped()
    {
        using var fixture = HygieneServer();
        var nova = await Settled(fixture, "nova", tile: 100, tick: 30 * GameDay);
        await SetActivity(fixture, nova.PlayerId, Now.AddDays(-30), Now.AddDays(-30));

        Assert.Equal(0, await Sweep(fixture));
    }

    [Fact]
    public async Task ColonyWithoutSaveIsArchived()
    {
        using var fixture = HygieneServer();
        var nova = await Idle(fixture, "nova", tile: 100, idleDays: 15, upload: false);

        Assert.Equal(1, await Sweep(fixture));
        Assert.Null((await Db(fixture, db => db.ArchivedColonies.SingleAsync(c => c.PlayerId == nova.PlayerId))).ColonyGameDays);
    }

    [Fact]
    public async Task SaveWithUnknownWealthIsSkipped()
    {
        using var fixture = HygieneServer();
        var nova = await Idle(fixture, "nova", tile: 100, idleDays: 15);
        await Db(fixture, db => db.Players.Where(p => p.Id == nova.PlayerId).ExecuteUpdateAsync(s => s.SetProperty(p => p.WealthTotal, 0L)));

        Assert.Equal(0, await Sweep(fixture));
    }

    [Fact]
    public async Task ActivityIsTheLaterOfLoginAndSessionEnd()
    {
        using var fixture = HygieneServer();
        var nova = await Idle(fixture, "nova", tile: 100, idleDays: 15);
        await SetActivity(fixture, nova.PlayerId, Now.AddDays(-20), Now.AddDays(-2));

        Assert.Equal(0, await Sweep(fixture));
    }

    [Fact]
    public async Task OneFailureDoesNotStopTheSweep()
    {
        using var fixture = HygieneServer();
        var nova = await Idle(fixture, "nova", tile: 100, idleDays: 15);
        var mira = await Idle(fixture, "mira", tile: 200, idleDays: 15);
        foreach (string file in Directory.GetFiles(Path.Combine(fixture.DataDir, "saves", nova.PlayerId.ToString()), "*.rws.zst")) File.Delete(file);

        Assert.Equal(1, await Sweep(fixture));

        Assert.False(await IsArchived(fixture, nova.PlayerId));
        Assert.True(await IsArchived(fixture, mira.PlayerId));
    }
}
