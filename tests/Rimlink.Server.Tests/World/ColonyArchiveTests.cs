using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Admin;
using Rimlink.Server.Application.Feed;
using Rimlink.Server.Application.World;
using Rimlink.Server.Storage;
using Rimlink.Server.Storage.Entities;
using Rimlink.Server.Tests.Support;
using Xunit;
using static Rimlink.Server.Tests.Support.GrpcTestExtensions;
using static Rimlink.Server.Tests.Support.HygieneFixtures;
using static Rimlink.Server.Tests.Support.SaveFixtures;

namespace Rimlink.Server.Tests.World;

/// <summary>A colony goes to the archive with its saves and comes back on the same tile (spec §2.4–2.5).</summary>
public class ColonyArchiveTests
{
    private static readonly Actor Admin = Actor.Api;

    private static async Task<ArchiveOutcome> Archive(ServerFixture fixture, long playerId, string reason = "admin")
    {
        using var scope = fixture.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ColonyArchive>().Archive(playerId, reason, Admin);
    }

    private static async Task<RestoreOutcome> Restore(ServerFixture fixture, long archiveId)
    {
        using var scope = fixture.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ColonyArchive>().Restore(archiveId, "api");
    }

    private static Task<ArchivedColony> Row(ServerFixture fixture, long id) =>
        Db(fixture, db => db.ArchivedColonies.AsNoTracking().SingleAsync(c => c.Id == id));

    [Fact]
    public async Task ArchiveMovesSavesAndFreesTheTile()
    {
        using var fixture = HygieneServer();
        var nova = await Settled(fixture, "nova", tile: 100, wealth: 50000, tick: 3000, uploads: 2);
        var mira = await fixture.RegisterAndLogin("mira", playerName: "Mira");
        using var stream = new Protocol.V1.Events.EventsClient(fixture.CreateChannel()).Subscribe(new Empty(), Bearer(mira.SessionToken));
        var settledAt = await Db(fixture, db => db.WorldObjects.Where(o => o.Id == nova.Settlement.Id).Select(o => o.CreatedAt).SingleAsync());

        var outcome = Assert.IsType<ArchiveOutcome.Archived>(await Archive(fixture, nova.PlayerId));

        Assert.Equal(0, await Db(fixture, db => db.SaveVersions.CountAsync(v => v.PlayerId == nova.PlayerId)));
        var row = await Row(fixture, outcome.ArchiveId);
        string dir = Path.Combine(fixture.DataDir, row.Dir);
        Assert.Equal(2, Directory.GetFiles(dir, "*.rws.zst").Length);
        Assert.True(File.Exists(Path.Combine(dir, "manifest.json")));
        Assert.Empty(Directory.GetFiles(Path.Combine(fixture.DataDir, "saves", nova.PlayerId.ToString()), "*.rws.zst"));
        Assert.Equal((100, "Nova Home", 50000L, ArchiveState.Archived, "admin", "api"), (row.Tile, row.Name, row.WealthTotal, row.State, row.Reason, row.ArchivedBy));
        Assert.Equal(settledAt, row.SettledAt);
        Assert.Equal(0, row.ColonyGameDays);
        var objects = await new Protocol.V1.World.WorldClient(fixture.CreateChannel()).ListObjectsAsync(new Empty(), Bearer(mira.SessionToken));
        Assert.DoesNotContain(objects.Objects, o => o.Id == nova.Settlement.Id);
        Assert.True(await NextEvent(stream, ServerEvent.BodyOneofCase.WorldObjectRemoved));
    }

    private static async Task<bool> NextEvent(AsyncServerStreamingCall<ServerEvent> stream, ServerEvent.BodyOneofCase kind)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (await stream.ResponseStream.MoveNext(timeout.Token))
            if (stream.ResponseStream.Current.BodyCase == kind) return true;
        return false;
    }

    [Fact]
    public async Task ArchiveCancelsRaidsAndReturnsPawns()
    {
        using var fixture = HygieneServer().WithSetting("Raids:NewbieProtectionDays", "0").WithSetting("Raids:MinColonyWealth", "0");
        var nova = await Settled(fixture, "nova", tile: 100);
        var mira = await Settled(fixture, "mira", tile: 200);
        await RaidFixtures.MakeTargetRaidable(fixture, mira.PlayerId);
        var raid = await RaidFixtures.RaidClientsOf(fixture, nova.Token).Raids.LaunchAsync(RaidFixtures.Launch(mira.PlayerId, tick: 2000), Bearer(nova.Token));

        await Archive(fixture, nova.PlayerId);

        Assert.Equal(RaidStateDb.Cancelled, await Db(fixture, db => db.Raids.Where(r => r.Id == raid.Id).Select(r => r.State).SingleAsync()));
        Assert.True(await Db(fixture, db => db.Parcels.AnyAsync(p => p.ToPlayerId == nova.PlayerId && p.Kind == ParcelKindDb.RaidReturn)));
    }

    [Fact]
    public async Task ArchiveLeavesWalletLotsParcelsAndTicks()
    {
        using var fixture = HygieneServer();
        var nova = await Settled(fixture, "nova", tile: 100);
        await Db(fixture, async db =>
        {
            await db.Players.Where(p => p.Id == nova.PlayerId).ExecuteUpdateAsync(s => s.SetProperty(p => p.LastOutgoingTick, 900L));
            db.Parcels.Add(new Server.Storage.Entities.Parcel { ToPlayerId = nova.PlayerId, Kind = ParcelKindDb.Gift, CreatedAt = DateTime.UtcNow, DeliveredAt = DateTime.UtcNow, DeliveredTick = 800 });
            db.Parcels.Add(new Server.Storage.Entities.Parcel { ToPlayerId = nova.PlayerId, Kind = ParcelKindDb.Gift, CreatedAt = DateTime.UtcNow });
            db.Lots.Add(new Server.Storage.Entities.Lot { SellerPlayerId = nova.PlayerId, Price = 10, CreatedAt = DateTime.UtcNow, ExpiresAt = DateTime.UtcNow.AddDays(30), Status = LotStatusDb.Open });
            return await db.SaveChangesAsync();
        });
        var before = await fixture.Rest().GetStringAsync($"/api/admin/economy/wallet/{nova.PlayerId}");

        await Archive(fixture, nova.PlayerId);

        Assert.Equal(before, await fixture.Rest().GetStringAsync($"/api/admin/economy/wallet/{nova.PlayerId}"));
        Assert.Equal(900, await Db(fixture, db => db.Players.Where(p => p.Id == nova.PlayerId).Select(p => p.LastOutgoingTick).SingleAsync()));
        Assert.Equal(800, await Db(fixture, db => db.Parcels.Where(p => p.ToPlayerId == nova.PlayerId && p.DeliveredAt != null).Select(p => p.DeliveredTick).SingleAsync()));
        Assert.Equal(1, await Db(fixture, db => db.Parcels.CountAsync(p => p.ToPlayerId == nova.PlayerId && p.DeliveredAt == null)));
        Assert.Equal(1, await Db(fixture, db => db.Lots.CountAsync(l => l.SellerPlayerId == nova.PlayerId && l.Status == LotStatusDb.Open)));
    }

    [Fact]
    public async Task ArchiveWithoutColonyIsRefused()
    {
        using var fixture = HygieneServer();
        var nova = await fixture.RegisterAndLogin("nova", playerName: "Nova");

        Assert.IsType<ArchiveOutcome.NoColony>(await Archive(fixture, nova.Player.PlayerId));
    }

    [Fact]
    public async Task ArchivingAnOnlinePlayerEndsTheSession()
    {
        using var fixture = HygieneServer();
        var nova = await Settled(fixture, "nova", tile: 100);
        using var stream = new Protocol.V1.Events.EventsClient(fixture.CreateChannel()).Subscribe(new Empty(), Bearer(nova.Token));
        // The stream is attached once it delivers something: queued events still reach it after the session ends.
        await fixture.RegisterAndLogin("mira", playerName: "Mira");
        Assert.True(await NextEvent(stream, ServerEvent.BodyOneofCase.PlayerJoined));

        Assert.IsType<ArchiveOutcome.Archived>(await Archive(fixture, nova.PlayerId));

        Assert.True(await NextEvent(stream, ServerEvent.BodyOneofCase.Notice));
        Assert.Null(fixture.Services.GetRequiredService<Application.Sessions.SessionRegistry>().ByPlayer(nova.PlayerId));
    }

    [Fact]
    public async Task ArchiveIsRefusedWhileFilesAreMissing()
    {
        using var fixture = HygieneServer();
        var nova = await Settled(fixture, "nova", tile: 100);
        foreach (string file in Directory.GetFiles(Path.Combine(fixture.DataDir, "saves", nova.PlayerId.ToString()), "*.rws.zst")) File.Delete(file);

        await Assert.ThrowsAnyAsync<Exception>(() => Archive(fixture, nova.PlayerId));

        Assert.Equal(1, await Db(fixture, db => db.SaveVersions.CountAsync(v => v.PlayerId == nova.PlayerId)));
        Assert.True(await Db(fixture, db => db.WorldObjects.AnyAsync(o => o.OwnerPlayerId == nova.PlayerId)));
        Assert.Equal(0, await Db(fixture, db => db.ArchivedColonies.CountAsync()));
    }

    [Fact]
    public async Task RestoreBringsBackTheSameSaves()
    {
        using var fixture = HygieneServer();
        var nova = await Settled(fixture, "nova", tile: 100, tick: 3000, uploads: 2);
        var versionsBefore = await Db(fixture, db => db.SaveVersions.AsNoTracking().Where(v => v.PlayerId == nova.PlayerId).OrderBy(v => v.Number)
            .Select(v => new { v.Number, v.GameTick, v.Size, v.IsCurrent }).ToListAsync());
        var archived = Assert.IsType<ArchiveOutcome.Archived>(await Archive(fixture, nova.PlayerId));
        string dir = Path.Combine(fixture.DataDir, (await Row(fixture, archived.ArchiveId)).Dir);

        var restored = Assert.IsType<RestoreOutcome.Restored>(await Restore(fixture, archived.ArchiveId));

        Assert.Equal((100, "Nova Home"), (restored.Settlement.Tile, restored.Settlement.Name));
        var versionsAfter = await Db(fixture, db => db.SaveVersions.AsNoTracking().Where(v => v.PlayerId == nova.PlayerId).OrderBy(v => v.Number)
            .Select(v => new { v.Number, v.GameTick, v.Size, v.IsCurrent }).ToListAsync());
        Assert.Equal(versionsBefore, versionsAfter);
        var relogin = await fixture.RegisterAndLogin("nova");
        var (_, data) = await Download(new Protocol.V1.Saves.SavesClient(fixture.CreateChannel()), relogin.SessionToken);
        Assert.Equal(nova.LastSave, data);
        var row = await Row(fixture, archived.ArchiveId);
        Assert.Equal((ArchiveState.Restored, "api"), (row.State, row.RestoredBy));
        Assert.NotNull(row.RestoredAt);
        Assert.False(Directory.Exists(dir));
    }

    [Fact]
    public async Task RestoreOfUnknownArchiveIsNotFound()
    {
        using var fixture = HygieneServer();

        Assert.IsType<RestoreOutcome.NotFound>(await Restore(fixture, 12345));
    }

    [Fact]
    public async Task RestoreTwiceIsAlreadyRestored()
    {
        using var fixture = HygieneServer();
        var nova = await Settled(fixture, "nova", tile: 100);
        var archived = Assert.IsType<ArchiveOutcome.Archived>(await Archive(fixture, nova.PlayerId));
        await Restore(fixture, archived.ArchiveId);

        Assert.IsType<RestoreOutcome.AlreadyRestored>(await Restore(fixture, archived.ArchiveId));
    }

    [Fact]
    public async Task RestoreOnATakenTileIsRefused()
    {
        using var fixture = HygieneServer();
        var nova = await Settled(fixture, "nova", tile: 100);
        var archived = Assert.IsType<ArchiveOutcome.Archived>(await Archive(fixture, nova.PlayerId));
        await Settled(fixture, "mira", tile: 100);

        Assert.IsType<RestoreOutcome.TileTaken>(await Restore(fixture, archived.ArchiveId));
        Assert.Equal(ArchiveState.Archived, (await Row(fixture, archived.ArchiveId)).State);
        Assert.Equal(0, await Db(fixture, db => db.SaveVersions.CountAsync(v => v.PlayerId == nova.PlayerId)));
    }

    [Fact]
    public async Task RestoreForAPlayerWithANewColonyIsRefused()
    {
        using var fixture = HygieneServer();
        var nova = await Settled(fixture, "nova", tile: 100);
        var archived = Assert.IsType<ArchiveOutcome.Archived>(await Archive(fixture, nova.PlayerId));
        await Settled(fixture, "mira", tile: 100); // keeps the login below from bringing the old colony back
        var relogin = await fixture.RegisterAndLogin("nova");
        await new Protocol.V1.World.WorldClient(fixture.CreateChannel()).SettleAsync(new SettleRequest { Tile = 300, Name = "New Home" }, Bearer(relogin.SessionToken));

        Assert.IsType<RestoreOutcome.PlayerHasColony>(await Restore(fixture, archived.ArchiveId));
    }

    [Fact]
    public async Task RestoreOfADamagedArchiveChangesNothing()
    {
        using var fixture = HygieneServer();
        var nova = await Settled(fixture, "nova", tile: 100);
        var archived = Assert.IsType<ArchiveOutcome.Archived>(await Archive(fixture, nova.PlayerId));
        string dir = Path.Combine(fixture.DataDir, (await Row(fixture, archived.ArchiveId)).Dir);
        string file = Directory.GetFiles(dir, "*.rws.zst").Single();
        File.WriteAllBytes(file, [1, 2, 3]);

        Assert.IsType<RestoreOutcome.Damaged>(await Restore(fixture, archived.ArchiveId));

        Assert.Equal(ArchiveState.Archived, (await Row(fixture, archived.ArchiveId)).State);
        Assert.Equal(0, await Db(fixture, db => db.SaveVersions.CountAsync(v => v.PlayerId == nova.PlayerId)));
        Assert.False(await Db(fixture, db => db.WorldObjects.AnyAsync(o => o.OwnerPlayerId == nova.PlayerId)));
        Assert.Empty(Directory.GetFiles(Path.Combine(fixture.DataDir, "saves", nova.PlayerId.ToString()), "*.rws.zst"));
    }

    [Fact]
    public async Task ArchiveLeavesSavesUsableWhenTheCommitFails()
    {
        using var fixture = HygieneServer();
        var nova = await Settled(fixture, "nova", tile: 100);
        // The archive row's insert fails inside the transaction: everything it wrote rolls back.
        await Db(fixture, db => db.Database.ExecuteSqlRawAsync(
            "CREATE TRIGGER fail_archive BEFORE INSERT ON archived_colonies BEGIN SELECT RAISE(ABORT, 'boom'); END;"));

        await Assert.ThrowsAnyAsync<Exception>(() => Archive(fixture, nova.PlayerId));

        Assert.Equal(1, await Db(fixture, db => db.SaveVersions.CountAsync(v => v.PlayerId == nova.PlayerId)));
        Assert.True(await Db(fixture, db => db.WorldObjects.AnyAsync(o => o.OwnerPlayerId == nova.PlayerId)));
        var relogin = await fixture.RegisterAndLogin("nova");
        var (_, data) = await Download(new Protocol.V1.Saves.SavesClient(fixture.CreateChannel()), relogin.SessionToken);
        Assert.Equal(nova.LastSave, data);
    }

    [Fact]
    public async Task ArchiveAndRestoreArePublishedToTheFeed()
    {
        using var fixture = HygieneServer();
        var nova = await Settled(fixture, "nova", tile: 100);
        using var probe = fixture.Probe();

        var archived = Assert.IsType<ArchiveOutcome.Archived>(await Archive(fixture, nova.PlayerId, reason: "auto"));
        var archivedEvent = FeedProbe.Data(await probe.Next(FeedTypes.ColonyArchived));
        await Restore(fixture, archived.ArchiveId);
        var restoredEvent = FeedProbe.Data(await probe.Next(FeedTypes.ColonyRestored));

        Assert.Contains(FeedTypes.ColonyArchived, FeedTypes.All);
        Assert.Contains(FeedTypes.ColonyRestored, FeedTypes.All);
        Assert.Equal("Nova", archivedEvent.GetProperty("player").GetProperty("name").GetString());
        Assert.Equal(("Nova Home", 100, "auto", "api"), (archivedEvent.GetProperty("colony").GetString(), archivedEvent.GetProperty("tile").GetInt32(),
            archivedEvent.GetProperty("reason").GetString(), archivedEvent.GetProperty("actor").GetString()));
        Assert.Equal(("Nova Home", "api"), (restoredEvent.GetProperty("colony").GetString(), restoredEvent.GetProperty("actor").GetString()));
    }
}
