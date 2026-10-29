using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Rimlink.Server.Application.Admin;
using Rimlink.Server.Application.Sessions;
using Rimlink.Server.Application.World;
using Rimlink.Server.Storage.Entities;
using Rimlink.Server.Tests.Support;
using Xunit;
using static Rimlink.Server.Tests.Support.HygieneFixtures;

namespace Rimlink.Server.Tests.World;

/// <summary>An archived colony comes back when its player logs in, unless its tile was taken (spec §2.6).</summary>
public class LoginRestoreTests
{
    private static async Task<long> ArchiveOffline(ServerFixture fixture, Colony colony)
    {
        var sessions = fixture.Services.GetRequiredService<SessionRegistry>();
        sessions.End(sessions.ByPlayer(colony.PlayerId)!.Token);
        using var scope = fixture.Services.CreateScope();
        var outcome = await scope.ServiceProvider.GetRequiredService<ColonyArchive>().Archive(colony.PlayerId, "auto", Actor.System("system:hygiene"));
        return Assert.IsType<ArchiveOutcome.Archived>(outcome).ArchiveId;
    }

    [Fact]
    public async Task LoginRestoresAnArchivedColony()
    {
        using var fixture = HygieneServer();
        var nova = await Settled(fixture, "nova", tile: 100);
        long archiveId = await ArchiveOffline(fixture, nova);

        var login = await fixture.RegisterAndLogin("nova");

        Assert.True(login.ColonyRestore.Restored);
        Assert.Equal(("Nova Home", 100), (login.ColonyRestore.Name, login.ColonyRestore.Tile));
        Assert.True(login.HasSave);
        Assert.True(login.HasSettlement);
        var row = await Db(fixture, db => db.ArchivedColonies.AsNoTracking().SingleAsync(c => c.Id == archiveId));
        Assert.Equal((ArchiveState.Restored, "player"), (row.State, row.RestoredBy));
        Assert.True(fixture.Services.GetRequiredService<SessionRegistry>().ByPlayer(nova.PlayerId)!.ColonyRestore!.Restored);
    }

    [Fact]
    public async Task LoginWithTheTileTakenStartsFresh()
    {
        using var fixture = HygieneServer();
        var nova = await Settled(fixture, "nova", tile: 100);
        await Db(fixture, db => db.Players.Where(p => p.Id == nova.PlayerId).ExecuteUpdateAsync(s => s.SetProperty(p => p.LastOutgoingTick, 900L)));
        long archiveId = await ArchiveOffline(fixture, nova);
        await Settled(fixture, "mira", tile: 100);

        var login = await fixture.RegisterAndLogin("nova");

        Assert.False(login.ColonyRestore.Restored);
        Assert.Equal(("tile_taken", "Nova Home"), (login.ColonyRestore.Reason, login.ColonyRestore.Name));
        Assert.False(login.HasSave);
        Assert.False(login.HasSettlement);
        Assert.Equal(0, await Db(fixture, db => db.Players.Where(p => p.Id == nova.PlayerId).Select(p => p.LastOutgoingTick).SingleAsync()));
        Assert.Equal(ArchiveState.Archived, await Db(fixture, db => db.ArchivedColonies.Where(c => c.Id == archiveId).Select(c => c.State).SingleAsync()));
    }

    [Fact]
    public async Task LoginWithoutArchiveHasNoColonyRestore()
    {
        using var fixture = HygieneServer();
        await Settled(fixture, "nova", tile: 100);

        var login = await fixture.RegisterAndLogin("nova");

        Assert.Null(login.ColonyRestore);
    }

    [Fact]
    public async Task LoginAfterArchiveRestoresWhenTheSweepWonTheRace()
    {
        using var fixture = HygieneServer();
        var sessions = fixture.Services.GetRequiredService<SessionRegistry>();
        var now = DateTime.UtcNow;
        for (int i = 0; i < 10; i++)
        {
            var colony = await Settled(fixture, $"p{i}x", tile: 100 + i);
            sessions.End(sessions.ByPlayer(colony.PlayerId)!.Token);
            await Eventually(async () => await Db(fixture, db => db.Players.Where(p => p.Id == colony.PlayerId).Select(p => p.Account.LastSessionEndAt).SingleAsync()) != null);
            await SetActivity(fixture, colony.PlayerId, now.AddDays(-30), now.AddDays(-30));

            using var scope = fixture.Services.CreateScope();
            var sweep = scope.ServiceProvider.GetRequiredService<ColonyHygiene>().SweepDue(now);
            var login = fixture.RegisterAndLogin($"p{i}x");
            await Task.WhenAll(sweep, login);

            // Whichever came first, the player who just logged in is sent into a colony, not to a new one.
            Assert.True(login.Result.HasSettlement, $"round {i}");
            Assert.True(login.Result.HasSave, $"round {i}");
            Assert.True(await Db(fixture, db => db.WorldObjects.AnyAsync(o => o.OwnerPlayerId == colony.PlayerId)), $"round {i}");
        }
    }
}
