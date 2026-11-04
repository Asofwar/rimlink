using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Admin;
using Rimlink.Server.Application.Events;
using Rimlink.Server.Application.Feed;
using Rimlink.Server.Application.Influence;
using Rimlink.Protocol;
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
    /// <summary>The player is online: only offline players are archived (their next login would restore the colony at once).</summary>
    public sealed record PlayerOnline : ArchiveOutcome;
}

public abstract record RestoreOutcome
{
    public sealed record Restored(Protocol.V1.WorldObject Settlement, string InfluenceNoticeKey = "") : RestoreOutcome;
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
    Incidents.IncidentService incidents,
    Procurement.ProcurementService procurement,

    Auctions.AuctionService auctions,
    SaveArchive files,
    SaveFiles saveFiles,
    WorldService worlds,
    DataDirectory data,
    ColonyLocks locks,
    TimeProvider time,
    ILogger<ColonyArchive> log,
    InfluenceService influence,
    Outposts.OutpostTileClaims outpostTiles,
    Outposts.OutpostAuthority outpostAuthority,
    Outposts.PawnCustodyBridge custody)
{
    private const int SqliteConstraint = 19;

    public async Task<ArchiveOutcome> Archive(long playerId, string reason, Actor actor)
    {
        await using var held = await AcquireArchiveLockAsync(playerId);
        if (held.Authority is { } authorityLease)
        {
            if ((await outpostAuthority.CheckHeldAsync(authorityLease, default)).State != Outposts.OutpostStartupState.Ready
                || await db.OutpostDeliveryAttempts.AnyAsync(a => a.PlayerId == playerId && a.Active))
                throw Grpc.RpcErrors.Failed(ErrorCode.RecoveryRequired, Grpc.RefusalKeys.OutpostsOperation, "outpost-delivery-unresolved-before-archive");
        }
        var settlement = await db.WorldObjects.Include(o => o.Owner).ThenInclude(p => p.Account)
            .FirstOrDefaultAsync(o => o.OwnerPlayerId == playerId && o.Type == WorldObjectTypes.Settlement);
        if (settlement is null) return new ArchiveOutcome.NoColony();
        // Never while the player plays: a client whose session ends logs in again at once, and that login restores the colony.
        if (sessions.ByPlayer(playerId) is not null) return new ArchiveOutcome.PlayerOnline();
        var player = settlement.Owner;
        var versions = await db.SaveVersions.Where(v => v.PlayerId == playerId).OrderBy(v => v.Number).ToListAsync();
        files.EnsureFilesExist(playerId, versions); // before any side effect: a colony whose saves are gone stays as it is

        await using var cleanup = held.Authority is null ? null : custody.UseHeldEntry(held.Authority);
        await raids.CancelAllOf(playerId, "colony archived", returnOwnPawns: true, cleanup);
        await visits.EndAllOf(playerId);
        await live.EndAllOf(playerId, LiveEndReason.EndAdmin, cleanup);
        await incidents.CancelAllOf(playerId, "colony archived");
        await procurement.CancelAllOf(playerId);

        await auctions.CancelAllOf(playerId, "colony archived");

        var now = time.GetUtcNow().UtcDateTime;
        string relativeDir = Path.Combine("archive", $"colony-{playerId}-{now:yyyyMMddHHmmss}");
        string dir = Path.Combine(data.Root, relativeDir);
        var current = versions.FirstOrDefault(v => v.IsCurrent);
        var row = new ArchivedColony
        {
            PlayerId = playerId,
            SaveGeneration = player.SaveGeneration,
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

        Storage.Entities.World? influenceWorld = null;
        try
        {
            await files.CopyOut(versions, dir);
            await using var tx = await db.Database.BeginTransactionAsync();
            // A leave or force-disable may have committed while the files were copied.
            await db.Entry(settlement).ReloadAsync();
            InfluenceLifecycle.Copy(settlement, row);
            row.InfluenceSettlementId = settlement.Id;
            influenceWorld = await InfluenceLifecycle.Advance(db);
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

        TryDeleteFiles(playerId, versions.Select(v => v.Number));
        if (influenceWorld is not null) influence.Notify(influenceWorld);
        events.Broadcast(new ServerEvent { WorldObjectRemoved = new WorldObjectRemoved { ObjectId = settlement.Id } });
        feed.Publish(FeedTypes.ColonyArchived, new ColonyArchivedData(new FeedPlayer(playerId, player.Name), row.Name, row.Tile, reason, actor.Name));
        await audit.Write(actor, "archive", player.AccountId, $"{row.Name} on tile {row.Tile} ({reason})");
        HygieneMetrics.Archived.WithLabels(reason).Inc();
        log.LogInformation("Colony '{Colony}' of {Player} archived ({Reason}, by {Actor}) into {Dir}", row.Name, player.Name, reason, actor.Name, relativeDir);
        return new ArchiveOutcome.Archived(row.Id);
    }

    // Legacy colonies retain O -> S. A protected history must release both before taking A -> O -> S.
    // Recheck under O/S closes the first-history race without ever acquiring A under an owner lock.
    private async Task<ArchiveLock> AcquireArchiveLockAsync(long playerId)
    {
        IDisposable? owner = await locks.Hold(playerId);
        IDisposable? storage = null;
        try
        {
            storage = await saveFiles.PerStoreLock.HoldAsync();
            long[] affected = await CleanupOwners(playerId);
            bool protectedHistory = await db.OutpostSnapshotLinks.AnyAsync(p => affected.Contains(p.PlayerId))
                || await db.PawnCustody.AnyAsync(p => affected.Contains(p.PlayerId))
                || await db.OutpostDeliveryAttempts.AnyAsync(p => affected.Contains(p.PlayerId));
            if (!protectedHistory) return new(owner, storage, null);
            storage.Dispose(); storage = null; owner.Dispose(); owner = null;
            while (true)
            {
                var lease = await outpostAuthority.AcquireAsync(affected, default);
                long[] current;
                try
                {
                    current = await CleanupOwners(playerId);
                    if (current.All(lease.CoversOwner)) return new(null, null, lease);
                }
                catch { await lease.DisposeAsync(); throw; }
                await lease.DisposeAsync(); affected = current; // A is never acquired or expanded while O/S is held.
            }
        }
        catch { storage?.Dispose(); owner?.Dispose(); throw; }
    }

    private async Task<long[]> CleanupOwners(long playerId)
    {
        var raidOwners = await db.Raids.AsNoTracking().Where(r => (r.AttackerPlayerId == playerId || r.TargetPlayerId == playerId) && !RaidService.FinalStates.Contains(r.State))
            .Select(r => new { Actor = r.AttackerPlayerId, Other = r.TargetPlayerId }).ToListAsync();
        var liveOwners = await db.LiveSessions.AsNoTracking().Where(s => (s.GuestPlayerId == playerId || s.HostPlayerId == playerId) && !LiveSessionService.FinalStates.Contains(s.State))
            .Select(s => new { Actor = s.GuestPlayerId, Other = s.HostPlayerId }).ToListAsync();
        return raidOwners.Concat(liveOwners).SelectMany(p => new[] { p.Actor, p.Other }).Append(playerId).Where(id => id > 0).Distinct().Order().ToArray();
    }

    private sealed class ArchiveLock(IDisposable? owner, IDisposable? storage, Outposts.OutpostLease? authority) : IAsyncDisposable
    {
        public Outposts.OutpostLease? Authority { get; } = authority;
        public async ValueTask DisposeAsync()
        {
            if (Authority is not null) await Authority.DisposeAsync();
            storage?.Dispose(); owner?.Dispose();
        }
    }

    /// <param name="restoredBy"><c>player</c> for the automatic restore at login, otherwise the admin actor's name.</param>
    public async Task<RestoreOutcome> Restore(long archiveId, string restoredBy)
    {
        long? playerId = await db.ArchivedColonies.Where(c => c.Id == archiveId).Select(c => (long?)c.PlayerId).FirstOrDefaultAsync();
        if (playerId is null) return new RestoreOutcome.NotFound();
        using var held = await locks.Hold(playerId.Value);
        return await RestoreHeld(archiveId, restoredBy);
    }

    /// <summary>
    /// At login, before the reply is built: the player's latest archived colony comes back if they have no colony now.
    /// When it cannot (tile taken, damaged files) the player starts a new colony, which begins at tick 0, so the transfer stamp
    /// and redelivery marks of the old one are cleared as on a reset. Null when there was nothing to restore.
    /// </summary>
    public async Task<ColonyRestore?> RestoreOnLogin(long playerId)
    {
        using var held = await locks.Hold(playerId); // waits for an archive of this player still in progress
        if (await db.WorldObjects.AnyAsync(o => o.OwnerPlayerId == playerId && o.Type == WorldObjectTypes.Settlement)) return null;
        var generation = await db.Players.Where(p => p.Id == playerId).Select(p => p.SaveGeneration).SingleAsync();
        var latest = await db.ArchivedColonies.AsNoTracking()
            .Where(c => c.PlayerId == playerId && c.State == ArchiveState.Archived && c.SaveGeneration == generation)
            .OrderByDescending(c => c.ArchivedAt).ThenByDescending(c => c.Id)
            .FirstOrDefaultAsync();
        if (latest is null) return null;

        var reply = new ColonyRestore
        {
            Name = latest.Name,
            Tile = latest.Tile,
            ArchivedAt = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTime(DateTime.SpecifyKind(latest.ArchivedAt, DateTimeKind.Utc)),
        };
        RestoreOutcome outcome;
        try
        {
            outcome = await RestoreHeld(latest.Id, "player");
        }
        catch (Exception ex)
        {
            // Whatever goes wrong here must not keep the player out: the login goes on as for a damaged archive.
            log.LogError(ex, "Restoring archived colony #{Id} of player {PlayerId} at login failed", latest.Id, playerId);
            outcome = new RestoreOutcome.Damaged();
        }
        switch (outcome)
        {
            case RestoreOutcome.Restored restored:
                reply.Restored = true;
                reply.InfluenceNoticeKey = restored.InfluenceNoticeKey;
                return reply;
            case RestoreOutcome.TileTaken:
                reply.Reason = "tile_taken";
                break;
            case RestoreOutcome.Damaged:
                reply.Reason = "damaged";
                log.LogError("Archived colony #{Id} of player {PlayerId} could not be restored: its files are missing or damaged", latest.Id, playerId);
                break;
            default:
                return null;
        }
        await db.Players.Where(p => p.Id == playerId).ExecuteUpdateAsync(s => s.SetProperty(p => p.LastOutgoingTick, 0L));
        await db.Parcels.Where(p => p.ToPlayerId == playerId && p.DeliveredAt != null).ExecuteUpdateAsync(s => s.SetProperty(p => p.DeliveredTick, 0L));
        return reply;
    }

    private async Task<RestoreOutcome> RestoreHeld(long archiveId, string restoredBy)
    {
        using var storage = await saveFiles.PerStoreLock.HoldAsync();
        var row = await db.ArchivedColonies.Include(c => c.Player).FirstOrDefaultAsync(c => c.Id == archiveId);
        if (row is null) return new RestoreOutcome.NotFound();
        // Auth may already track Player from before a reset; Include must not decide from that cached instance.
        long generation = await db.Players.Where(p => p.Id == row.PlayerId).Select(p => p.SaveGeneration).SingleAsync();
        if (row.SaveGeneration != generation) return new RestoreOutcome.NotFound();
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
        Storage.Entities.World? influenceWorld = null;
        string influenceNotice = "";
        try
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            await db.Entry(row).ReloadAsync();
            InfluenceLifecycle.Copy(row, settlement);
            var world = await db.World.AsNoTracking().SingleOrDefaultAsync();
            long allianceId = await db.Set<AllianceMember>().Where(m => m.PlayerId == row.PlayerId && db.Set<AllianceGroup>().Any(g => g.Id == m.AllianceId && !g.Disbanded)).Select(m => m.AllianceId).SingleOrDefaultAsync();
            var context = new InfluenceContext { WorldInstanceId = world?.InfluenceInstanceId ?? "", SaveGeneration = generation, AllianceId = allianceId };
            if (settlement.InfluenceEnabled && !InfluenceService.Eligible(settlement, context, settlement.Tile))
                InfluenceLifecycle.Revoke(settlement);
            if (settlement.InfluenceEnabled && await db.WorldObjects.CountAsync(o => o.InfluenceEnabled) >= InfluenceLimits.MaxSources)
            {
                settlement.InfluenceEnabled = false;
                influenceNotice = "Influence.CapacityRestore";
            }
            influenceWorld = await InfluenceLifecycle.Advance(db);
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
            TryDeleteFiles(row.PlayerId, versions.Select(v => v.Number));
            return new RestoreOutcome.TileTaken();
        }
        catch
        {
            db.ChangeTracker.Clear();
            TryDeleteFiles(row.PlayerId, versions.Select(v => v.Number));
            throw;
        }

        TryDeleteDirectory(dir);
        if (influenceWorld is not null) influence.Notify(influenceWorld);
        settlement.Owner ??= row.Player;
        var proto = (await worlds.Describe([settlement]))[0];
        events.Broadcast(new ServerEvent { WorldObjectAdded = new WorldObjectAdded { Object = proto } });
        feed.Publish(FeedTypes.ColonyRestored, new ColonyRestoredData(new FeedPlayer(row.PlayerId, row.Player.Name), row.Name, row.Tile, restoredBy));
        HygieneMetrics.Restored.WithLabels(restoredBy == "player" ? "player" : "admin").Inc();
        log.LogInformation("Colony '{Colony}' of {Player} restored on tile {Tile} (by {By})", row.Name, row.Player.Name, row.Tile, restoredBy);
        return new RestoreOutcome.Restored(proto, influenceNotice);
    }

    private async Task<bool> TileTaken(int tile)
    {
        if (await outpostTiles.IsOutpostOccupiedHeldAsync(tile) ||
            await db.WorldObjects.AnyAsync(o => o.Tile == tile && o.Type == WorldObjectTypes.Settlement)) return true;
        var world = await db.World.AsNoTracking().FirstOrDefaultAsync();
        return world is null || WorldService.NpcTilesOf(world).Contains(tile);
    }

    /// <summary>Deleting is the last step: a file held open (a backup or download reading it) stays behind and is only logged.</summary>
    private void TryDeleteFiles(long playerId, IEnumerable<int> numbers)
    {
        try
        {
            files.DeleteFiles(playerId, numbers);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogWarning(ex, "Could not delete save files of player {PlayerId}; they stay until overwritten", playerId);
        }
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

    public async Task<IDisposable> Hold(long playerId, CancellationToken cancellationToken = default)
    {
        var gate = locks.GetOrAdd(playerId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
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
