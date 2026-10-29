using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Admin;
using Rimlink.Server.Application.Events;
using Rimlink.Server.Application.Feed;
using Rimlink.Server.Application.Live;
using Rimlink.Server.Application.Raids;
using Rimlink.Server.Application.Saves;
using Rimlink.Server.Application.Sessions;
using Rimlink.Server.Application.Visits;
using Rimlink.Server.Storage;
using Rimlink.Server.Storage.Entities;

namespace Rimlink.Server.Application.World;

public abstract record ArchiveOutcome
{
    public sealed record Archived(long ArchiveId) : ArchiveOutcome;
    public sealed record NoColony : ArchiveOutcome;
}

public abstract record RestoreOutcome
{
    public sealed record Restored(Protocol.V1.WorldObject Settlement) : RestoreOutcome;
    public sealed record NotFound : RestoreOutcome;
    public sealed record AlreadyRestored : RestoreOutcome;
    public sealed record PlayerHasColony : RestoreOutcome;
    public sealed record TileTaken : RestoreOutcome;
    public sealed record Damaged : RestoreOutcome;
}

/// <summary>
/// Moves a colony out of the world with its save files and brings it back on the same tile (spec §2.4–2.5).
/// Files are copied and verified first, the database commits, the sources are deleted last: a failure at any step leaves
/// either the live colony or a complete archive.
/// </summary>
public sealed class ColonyArchive(
    RimlinkDbContext db,
    SessionRegistry sessions,
    EventBus events,
    AdminFeed feed,
    AdminAudit audit,
    RaidService raids,
    VisitService visits,
    LiveSessionService live,
    SaveArchive files,
    DataDirectory data,
    ColonyLocks locks,
    TimeProvider time,
    ILogger<ColonyArchive> log)
{
    private const int SqliteConstraint = 19;

    public async Task<ArchiveOutcome> Archive(long playerId, string reason, Actor actor)
    {
        using var held = await locks.Hold(playerId);
        var settlement = await db.WorldObjects.Include(o => o.Owner).ThenInclude(p => p.Account)
            .FirstOrDefaultAsync(o => o.OwnerPlayerId == playerId && o.Type == WorldObjectTypes.Settlement);
        if (settlement is null) return new ArchiveOutcome.NoColony();
        var player = settlement.Owner;
        var versions = await db.SaveVersions.Where(v => v.PlayerId == playerId).OrderBy(v => v.Number).ToListAsync();
        files.EnsureFilesExist(playerId, versions); // before any side effect: a colony whose saves are gone stays as it is

        if (sessions.ByPlayer(playerId) is { } session)
        {
            events.SendTo(playerId, new ServerEvent { Notice = new Notice { Text = "Your colony was moved to the archive by an admin" } });
            sessions.End(session.Token);
        }
        await raids.CancelAllOf(playerId, "colony archived", returnOwnPawns: true);
        await visits.EndAllOf(playerId);
        await live.EndAllOf(playerId, LiveEndReason.EndAdmin);

        var now = time.GetUtcNow().UtcDateTime;
        string relativeDir = Path.Combine("archive", $"colony-{playerId}-{now:yyyyMMddHHmmss}");
        string dir = Path.Combine(data.Root, relativeDir);
        var current = versions.FirstOrDefault(v => v.IsCurrent);
        var row = new ArchivedColony
        {
            PlayerId = playerId,
            Tile = settlement.Tile,
            Name = settlement.Name,
            SettledAt = settlement.CreatedAt,
            ArchivedAt = now,
            Reason = reason,
            ArchivedBy = actor.Name,
            WealthTotal = player.WealthTotal,
            ColonyGameDays = current is null ? null : (int)(current.GameTick / GameTime.TicksPerDay),
            LastActiveAt = Activity.Of(player.Account),
            Dir = relativeDir,
            State = ArchiveState.Archived,
        };

        try
        {
            await files.CopyOut(versions, dir);
            await using var tx = await db.Database.BeginTransactionAsync();
            db.SaveVersions.RemoveRange(versions);
            db.WorldObjects.Remove(settlement);
            db.ArchivedColonies.Add(row);
            await db.SaveChangesAsync();
            await tx.CommitAsync();
        }
        catch
        {
            db.ChangeTracker.Clear();
            TryDeleteDirectory(dir);
            throw;
        }

        files.DeleteFiles(playerId, versions.Select(v => v.Number));
        events.Broadcast(new ServerEvent { WorldObjectRemoved = new WorldObjectRemoved { ObjectId = settlement.Id } });
        feed.Publish(FeedTypes.ColonyArchived, new ColonyArchivedData(new FeedPlayer(playerId, player.Name), row.Name, row.Tile, reason, actor.Name));
        await audit.Write(actor, "archive", player.AccountId, $"{row.Name} on tile {row.Tile} ({reason})");
        HygieneMetrics.Archived.WithLabels(reason).Inc();
        log.LogInformation("Colony '{Colony}' of {Player} archived ({Reason}, by {Actor}) into {Dir}", row.Name, player.Name, reason, actor.Name, relativeDir);
        return new ArchiveOutcome.Archived(row.Id);
    }

    /// <param name="restoredBy"><c>player</c> for the automatic restore at login, otherwise the admin actor's name.</param>
    public async Task<RestoreOutcome> Restore(long archiveId, string restoredBy)
    {
        long? playerId = await db.ArchivedColonies.Where(c => c.Id == archiveId).Select(c => (long?)c.PlayerId).FirstOrDefaultAsync();
        if (playerId is null) return new RestoreOutcome.NotFound();
        using var held = await locks.Hold(playerId.Value);
        var row = await db.ArchivedColonies.Include(c => c.Player).FirstAsync(c => c.Id == archiveId);
        if (row.State == ArchiveState.Restored) return new RestoreOutcome.AlreadyRestored();
        if (await db.WorldObjects.AnyAsync(o => o.OwnerPlayerId == row.PlayerId && o.Type == WorldObjectTypes.Settlement)
            || await db.SaveVersions.AnyAsync(v => v.PlayerId == row.PlayerId))
            return new RestoreOutcome.PlayerHasColony();
        if (await TileTaken(row.Tile)) return new RestoreOutcome.TileTaken();

        string dir = Path.Combine(data.Root, row.Dir);
        var versions = await files.CopyIn(row.PlayerId, dir);
        if (versions is null) return new RestoreOutcome.Damaged();

        var settlement = new Storage.Entities.WorldObject
        {
            Type = WorldObjectTypes.Settlement,
            OwnerPlayerId = row.PlayerId,
            Tile = row.Tile,
            Name = row.Name,
            CreatedAt = row.SettledAt,
        };
        try
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            db.SaveVersions.AddRange(versions);
            db.WorldObjects.Add(settlement);
            row.State = ArchiveState.Restored;
            row.RestoredAt = time.GetUtcNow().UtcDateTime;
            row.RestoredBy = restoredBy;
            await db.SaveChangesAsync();
            await tx.CommitAsync();
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqliteException { SqliteErrorCode: SqliteConstraint })
        {
            // Someone settled the tile between the check and the commit.
            db.ChangeTracker.Clear();
            files.DeleteFiles(row.PlayerId, versions.Select(v => v.Number));
            return new RestoreOutcome.TileTaken();
        }
        catch
        {
            db.ChangeTracker.Clear();
            files.DeleteFiles(row.PlayerId, versions.Select(v => v.Number));
            throw;
        }

        TryDeleteDirectory(dir);
        settlement.Owner ??= row.Player;
        var proto = WorldService.ToProto(settlement);
        events.Broadcast(new ServerEvent { WorldObjectAdded = new WorldObjectAdded { Object = proto } });
        feed.Publish(FeedTypes.ColonyRestored, new ColonyRestoredData(new FeedPlayer(row.PlayerId, row.Player.Name), row.Name, row.Tile, restoredBy));
        HygieneMetrics.Restored.WithLabels(restoredBy == "player" ? "player" : "admin").Inc();
        log.LogInformation("Colony '{Colony}' of {Player} restored on tile {Tile} (by {By})", row.Name, row.Player.Name, row.Tile, restoredBy);
        return new RestoreOutcome.Restored(proto);
    }

    private async Task<bool> TileTaken(int tile)
    {
        if (await db.WorldObjects.AnyAsync(o => o.Tile == tile && o.Type == WorldObjectTypes.Settlement)) return true;
        var world = await db.World.AsNoTracking().FirstOrDefaultAsync();
        return world is null || WorldService.NpcTilesOf(world).Contains(tile);
    }

    private void TryDeleteDirectory(string dir)
    {
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Could not delete {Dir}", dir);
        }
    }
}

/// <summary>One archive or restore at a time per player: the sweep, an admin and the player's own login never interleave.</summary>
public sealed class ColonyLocks
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, SemaphoreSlim> locks = new();

    public async Task<IDisposable> Hold(long playerId)
    {
        var gate = locks.GetOrAdd(playerId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        return new Release(gate);
    }

    private sealed class Release(SemaphoreSlim gate) : IDisposable
    {
        private int released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref released, 1) == 0) gate.Release();
        }
    }
}

public static class GameTime
{
    public const long TicksPerDay = 60000;
}
