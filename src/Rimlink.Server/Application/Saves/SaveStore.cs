using System.Security.Cryptography;
using System.Text.Json;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Events;
using Rimlink.Server.Application.Influence;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Grpc;
using Rimlink.Server.Storage;
using Rimlink.Server.Storage.Entities;
using ZstdSharp;

namespace Rimlink.Server.Application.Saves;

public sealed class SaveRejected(string key, string reason, params object[] args) : Exception(reason)
{
    public string Key { get; } = key;
    public object[] Args { get; } = args;
}

/// <summary>Versioned save storage: blobs under &lt;data&gt;/saves/&lt;playerId&gt; (see <see cref="SaveFiles"/>), metadata in save_versions.</summary>
public sealed class SaveStore(RimlinkDbContext db, DataDirectory data, SaveFiles files, SaveCompactor compactor, EventBus events, Integrity.IntegrityService integrity, World.WorldService worlds, ILogger<SaveStore> log, InfluenceService influence, Outposts.OutpostAuthority outpostAuthority, OutpostSnapshotGuard outpostGuard)
{
    public async Task<SaveVersion> Store(long playerId, SaveHeader header, IAsyncEnumerable<ReadOnlyMemory<byte>> chunks, CancellationToken cancellationToken,
        long? sessionEpoch = null, Func<bool>? sessionIsCurrent = null)
    {
        if (header.TotalSize <= 0 || header.TotalSize > Limits.MaxSaveBytes)
            throw new SaveRejected(RefusalKeys.SavesSize, $"save size {header.TotalSize} is outside 1..{Limits.MaxSaveBytes} bytes", header.TotalSize, Limits.MaxSaveBytes);
        if (header.Sha256.Length != 32) throw new SaveRejected(RefusalKeys.SavesNoHash, "header has no SHA-256");

        var world = await db.World.AsNoTracking().FirstOrDefaultAsync(cancellationToken) ?? throw RpcErrors.Failed(ErrorCode.WorldNotCreated, RefusalKeys.WorldNotCreated, "the world has not been created yet");
        long startedEpoch = sessionEpoch ?? await db.Players.Where(p => p.Id == playerId).Select(p => p.SaveWriteEpoch).SingleAsync(cancellationToken);

        string dir = data.Saves(playerId);
        string temp = Path.Combine(dir, $"upload-{Guid.NewGuid():N}.tmp");
        try
        {
            long written = 0;
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.Asynchronous))
            using (var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                await foreach (var chunk in chunks.WithCancellation(cancellationToken))
                {
                    written += chunk.Length;
                    if (written > header.TotalSize) throw new SaveRejected(RefusalKeys.SavesExtraData, "more data than the header announced");
                    await file.WriteAsync(chunk, cancellationToken);
                    sha.AppendData(chunk.Span);
                }
                if (written != header.TotalSize) throw new SaveRejected(RefusalKeys.SavesWrongSize, $"expected {header.TotalSize} bytes, got {written}", header.TotalSize, written);
                if (!sha.GetHashAndReset().AsSpan().SequenceEqual(header.Sha256.Span)) throw new SaveRejected(RefusalKeys.SavesHashMismatch, "SHA-256 does not match");
                file.Flush(flushToDisk: true);
            }

            if (SaveValidator.Validate(temp, world.Seed, out string? factionName) is { } reason) throw reason;
            SaveRecoveryData recovery;
            byte[] rawHash;
            try
            {
                using var file = File.OpenRead(temp);
                using var raw = new DecompressionStream(file);
                recovery = SaveRecoveryData.ReadXml(raw, out rawHash);
                if (recovery.Version != header.RecoveryVersion || recovery.SnapshotId != header.SnapshotId ||
                    recovery.Version > 0 && (recovery.Generation != header.SaveGeneration || recovery.WriteEpoch != header.SaveWriteEpoch))
                    throw new InvalidDataException("upload header does not describe its snapshot");
            }
            catch (Exception ex) when (ex is System.Xml.XmlException or IOException or InvalidDataException or ZstdException)
            { throw new SaveRejected(RefusalKeys.SavesUnreadable, "invalid recovery metadata: " + ex.Message, ex.Message); }
            // From here on the upload is complete: finish without the call token, so a client that drops right
            // after its last chunk does not leave a file without a row (or a row without a file).
            OutpostLease? outpostLease = await AcquireUploadLockAsync(playerId, recovery.Outposts);
            try
            {
                // Reset may have completed while the chunks arrived: compare a fresh row under the archive/write lock.
                var player = await db.Players.AsNoTracking().FirstAsync(p => p.Id == playerId);
                if (sessionIsCurrent is not null && !sessionIsCurrent()) throw RpcErrors.Unauthenticated("the upload session ended");
                if (header.SaveGeneration != player.SaveGeneration)
                    throw RpcErrors.With(StatusCode.FailedPrecondition, ErrorCode.SaveGenerationMismatch,
                        RefusalKeys.SavesGenerationMismatch, "this save belongs to a colony that was reset");
                if (header.SaveWriteEpoch != player.SaveWriteEpoch || startedEpoch != player.SaveWriteEpoch)
                    throw RpcErrors.With(StatusCode.FailedPrecondition, ErrorCode.SaveWriteEpochMismatch,
                        RefusalKeys.SavesWriteEpochMismatch, "this save predates an administrative rollback");
                if (player.RecoveryVersion > header.RecoveryVersion)
                    throw RpcErrors.With(StatusCode.FailedPrecondition, ErrorCode.RecoveryClientRequired,
                        RefusalKeys.RecoveryClientRequired, "the colony requires snapshot recovery support");
                if (outpostLease is not null)
                {
                    if ((await outpostAuthority.CheckHeldAsync(outpostLease, CancellationToken.None)).State != OutpostStartupState.Ready)
                        throw new OutpostEvidenceUnavailableException("outpost-authority-not-ready");
                    await outpostGuard.ValidateHeldAsync(outpostLease, playerId, header, recovery.Outposts, rawHash, CancellationToken.None);
                }
                if (recovery.Version > 0 && await db.SaveVersions.AsNoTracking().FirstOrDefaultAsync(v => v.PlayerId == playerId && v.SnapshotId == recovery.SnapshotId) is { } repeated)
                {
                    if (repeated.RawSha256 is null || !repeated.RawSha256.AsSpan().SequenceEqual(rawHash))
                        throw new SaveRejected(RefusalKeys.SavesHashMismatch, "snapshot identity was reused for different contents");
                    return repeated;
                }
                // Items given away after this tick are already elsewhere; accepting it would bring them back.
                if (header.GameTick < player.LastOutgoingTick)
                    throw RpcErrors.With(StatusCode.FailedPrecondition, ErrorCode.SaveStale, RefusalKeys.SavesStale, $"save tick {header.GameTick} is older than your last transfer at tick {player.LastOutgoingTick}", header.GameTick, player.LastOutgoingTick);
                var seenBefore = await worlds.DescribeSettlementOf(playerId);
                int number = (await db.SaveVersions.Where(v => v.PlayerId == playerId).MaxAsync(v => (int?)v.Number) ?? 0) + 1;
                // The integrity check compares with the version this upload replaces as current.
                var baseVersion = await db.SaveVersions.AsNoTracking().FirstOrDefaultAsync(v => v.PlayerId == playerId && v.IsCurrent);
                if (baseVersion is not null && header.GameTick < baseVersion.GameTick)
                    throw RpcErrors.With(StatusCode.FailedPrecondition, ErrorCode.SaveStale, RefusalKeys.SavesStale,
                        "this save is older than the current snapshot", header.GameTick, baseVersion.GameTick);
                string final = files.FullPath(playerId, number);

                var version = new SaveVersion
                {
                    PlayerId = playerId,
                    Number = number,
                    GameTick = header.GameTick,
                    Size = written,
                    Sha256 = header.Sha256.ToByteArray(),
                    CreatedAt = DateTime.UtcNow,
                    IsCurrent = true,
                    DevMode = header.DevMode,
                    WealthTotal = header.WealthTotal,
                    WealthItems = header.WealthItems,
                    SnapshotId = recovery.Version == 0 ? null : recovery.SnapshotId,
                    RecoveryVersion = recovery.Version, SaveGeneration = header.SaveGeneration, SaveWriteEpoch = header.SaveWriteEpoch,
                    RawSha256 = rawHash, RecoveryJson = recovery.Version == 0 ? null : JsonSerializer.Serialize(recovery),
                };
                async Task StageVersion(CancellationToken ct)
                {
                    await db.SaveVersions.Where(v => v.PlayerId == playerId && v.IsCurrent).ExecuteUpdateAsync(s => s.SetProperty(v => v.IsCurrent, false), ct);
                    db.SaveVersions.Add(version);
                    if (recovery.Version > player.RecoveryVersion)
                        await db.Players.Where(p => p.Id == playerId).ExecuteUpdateAsync(s => s.SetProperty(p => p.RecoveryVersion, recovery.Version));
                    // The faction name the player chose in game is the one others should see on the map.
                    if (factionName is not null && factionName != player.FactionName && Names.IsValid(factionName))
                        await db.Players.Where(p => p.Id == playerId).ExecuteUpdateAsync(s => s.SetProperty(p => p.FactionName, factionName));
                    // Raid protection reads these: age of the colony on this server and its wealth as of the last save.
                    // A header without wealth (the pending-upload path, no home map) reports 0: keep what the last save said.
                    var now = DateTime.UtcNow;
                    if (header.WealthTotal > 0)
                        await db.Players.Where(p => p.Id == playerId).ExecuteUpdateAsync(s => s
                            .SetProperty(p => p.WealthTotal, header.WealthTotal)
                            .SetProperty(p => p.WealthItems, header.WealthItems)
                            .SetProperty(p => p.FirstSaveAt, p => p.FirstSaveAt ?? now));
                    else
                        await db.Players.Where(p => p.Id == playerId).ExecuteUpdateAsync(s => s.SetProperty(p => p.FirstSaveAt, p => p.FirstSaveAt ?? now));
                    await db.SaveChangesAsync(ct);
                }
                // Bytes are flushed before their immutable link, custody and head become one protected decision.
                if (outpostLease is not null)
                {
                    File.Move(temp, final, overwrite: true);
                    var authority = await db.OutpostAuthority.AsNoTracking().SingleAsync();
                    await outpostAuthority.CommitHeldAsync(outpostLease, new TransitionIntent(
                        Guid.ParseExact(recovery.SnapshotId, "N").ToString("D"), authority.WorldId, playerId, header.SaveGeneration,
                        authority.RestoreBoundary, "SnapshotAccepted", $"save:{playerId}",
                        new(authority.HeadId, Google.Protobuf.ByteString.CopyFrom(authority.HeadDigest)),
                        Google.Protobuf.ByteString.CopyFrom(recovery.Outposts.Metadata.ToByteArray())), async (_, ct) =>
                    {
                        await StageVersion(ct);
                        await outpostGuard.StageAcceptedHeldAsync(outpostLease, version, recovery.Outposts, ct);
                    }, CancellationToken.None);
                }
                else
                {
                    await using var tx = await db.Database.BeginTransactionAsync();
                    await StageVersion(CancellationToken.None);
                    File.Move(temp, final, overwrite: true);
                    await tx.CommitAsync();
                }

                await ApplyRetention(playerId);
                // Others see a tier and an age on the map: tell them when either moved.
                if (await worlds.DescribeSettlementOf(playerId) is { } seenAfter
                    && (seenBefore is null || seenBefore.WealthTier != seenAfter.WealthTier || seenBefore.HasColonyGameDays != seenAfter.HasColonyGameDays
                        || seenBefore.ColonyGameDays != seenAfter.ColonyGameDays))
                    events.BroadcastExcept(playerId, new ServerEvent { WorldObjectUpdated = new WorldObjectUpdated { Object = seenAfter } });
                log.LogInformation("Stored save v{Version} for player {PlayerId}: {Size} bytes, tick {Tick}", number, playerId, written, header.GameTick);
                // Stored is stored: a failing check is logged and never refuses the upload.
                try { await integrity.OnStored(playerId, baseVersion, version); }
                catch (Exception ex) { log.LogError(ex, "Integrity check of save v{Version} for player {PlayerId} failed", number, playerId); }
                await TryCompact(playerId);
                return version;
            }
            finally
            {
                if (outpostLease is not null) await outpostLease.DisposeAsync();
                else files.PerStoreLock.Release();
            }
        }
        catch (OutpostEvidenceUnavailableException ex)
        { throw RpcErrors.Failed(ErrorCode.RecoveryRequired, RefusalKeys.RecoveryRequired, "protected snapshot evidence is unavailable: " + ex.Message); }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    private async Task<OutpostLease?> AcquireUploadLockAsync(long playerId, OutpostRecoveryData metadata)
    {
        if (metadata.Present || await outpostGuard.RequiresProtectionAsync(playerId, CancellationToken.None))
        {
            string[] ids = metadata.Metadata.Registry.Select(r => r.PawnCustodyId).ToArray();
            var sourceOwners = await db.PawnCustody.AsNoTracking().Where(p => ids.Contains(p.Id)).Select(p => p.PlayerId).ToListAsync();
            return await outpostAuthority.AcquireAsync(sourceOwners.Append(playerId).Distinct().ToArray(), CancellationToken.None);
        }
        await files.PerStoreLock.WaitAsync();
        try
        {
            if (!await outpostGuard.RequiresProtectionAsync(playerId, CancellationToken.None)) return null;
        }
        catch { files.PerStoreLock.Release(); throw; }
        files.PerStoreLock.Release();
        return await outpostAuthority.AcquireAsync([playerId], CancellationToken.None);
    }

    public async Task<(SaveVersion Version, Stream Content, long Generation, long WriteEpoch)?> OpenCurrent(long playerId)
    {
        await files.PerStoreLock.WaitAsync();
        try
        {
            var version = await db.SaveVersions.AsNoTracking().FirstOrDefaultAsync(v => v.PlayerId == playerId && v.IsCurrent);
            if (version is null) return null;
            // A backup's database can be older than its files: the version it calls current may have become a delta after the snapshot.
            if (!File.Exists(PathOf(version)) || files.Deltas(playerId, version.Number).Any())
            {
                if (!await EnsureFull(version)) return null;
            }
            var authority = await db.Players.AsNoTracking().Where(p => p.Id == playerId).Select(p => new { p.SaveWriteEpoch, p.SaveGeneration }).SingleAsync();
            var stream = new FileStream(PathOf(version), FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.Asynchronous | FileOptions.SequentialScan);
            return (version, stream, authority.SaveGeneration, authority.SaveWriteEpoch);
        }
        finally { files.PerStoreLock.Release(); }
    }

    public Task<List<SaveVersion>> List(long playerId) =>
        db.SaveVersions.AsNoTracking().Where(v => v.PlayerId == playerId).OrderBy(v => v.Number).ToListAsync();

    public Task<bool> HasSave(long playerId) => db.SaveVersions.AnyAsync(v => v.PlayerId == playerId && v.IsCurrent);

    public async Task<bool> Rollback(long playerId, int number)
    {
        await files.PerStoreLock.WaitAsync();
        try
        {
            return await RollbackHeld(playerId, number);
        }
        finally
        {
            files.PerStoreLock.Release();
        }
    }

    private async Task<bool> RollbackHeld(long playerId, int number)
    {
        var target = await db.SaveVersions.FirstOrDefaultAsync(v => v.PlayerId == playerId && v.Number == number);
        if (target is null || !await EnsureFull(target)) return false;

        await using var tx = await db.Database.BeginTransactionAsync();
        await db.SaveVersions.Where(v => v.PlayerId == playerId && v.IsCurrent && v.Id != target.Id).ExecuteUpdateAsync(s => s.SetProperty(v => v.IsCurrent, false));
        target.IsCurrent = true;
        // An older timeline may reuse a pawn id: revoke assignments in the same transaction as the rollback.
        await db.Set<ColonyMember>().Where(x => x.OwnerPlayerId == playerId).ExecuteUpdateAsync(s => s.SetProperty(x => x.PawnIds, ""));
        // An admin rolling back behind a transfer knowingly returns what was given away; the stale-save guard moves with it.
        var player = await db.Players.FirstAsync(p => p.Id == playerId);
        await db.Entry(player).ReloadAsync(); // an admin scope may have tracked the player before waiting for another rollback
        player.SaveWriteEpoch++;
        if (player.LastOutgoingTick > target.GameTick) player.LastOutgoingTick = target.GameTick;
        // Raid protection and loot caps read the player's wealth: it is the current version's (older versions did not keep it).
        if (target.WealthTotal > 0)
        {
            player.WealthTotal = target.WealthTotal;
            player.WealthItems = target.WealthItems;
        }
        await db.SaveChangesAsync();
        await tx.CommitAsync();

        events.SendTo(playerId, new ServerEvent { SaveRolledBack = new SaveRolledBack { Version = number, SaveWriteEpoch = player.SaveWriteEpoch } });
        log.LogInformation("Save of player {PlayerId} rolled back to v{Version}", playerId, number);
        await TryCompact(playerId);
        return true;
    }

    /// <summary>Stored is stored: a failing pass is logged and leaves full files behind.</summary>
    private async Task TryCompact(long playerId)
    {
        try { await compactor.Run(playerId); }
        catch (Exception ex) { log.LogError(ex, "Compacting the saves of player {PlayerId} failed", playerId); }
    }

    /// <summary>
    /// Gives the version a full file; false when it cannot be read back. A rebuilt file need not match the upload byte for byte, so the
    /// row takes its size and hash. The caller holds the store lock.
    /// </summary>
    private async Task<bool> EnsureFull(SaveVersion version)
    {
        try
        {
            switch (files.Locate(version.PlayerId, version.Number))
            {
                case SaveForm.Full:
                    if (files.RecoverFullMetadata(version.PlayerId, version.Number, version.Sha256) is not { } rebuilt) return true;
                    (version.Size, version.Sha256) = rebuilt;
                    break;
                case SaveForm.Delta delta:
                    (version.Size, version.Sha256) = files.Rebuild(delta, PathOf(version));
                    break;
                default:
                    return false;
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            log.LogError(ex, "Save v{Version} of player {PlayerId} cannot be rebuilt", version.Number, version.PlayerId);
            return false;
        }
        await db.SaveVersions.Where(v => v.Id == version.Id).ExecuteUpdateAsync(s => s.SetProperty(v => v.Size, version.Size).SetProperty(v => v.Sha256, version.Sha256));
        // Left behind, it is only a spare: the full file wins and the next pass removes it.
        try { files.DeleteDeltas(version.PlayerId, version.Number); }
        catch (IOException ex) { log.LogDebug(ex, "A delta of save v{Version} of player {PlayerId} stays until the next pass", version.Number, version.PlayerId); }
        return true;
    }

    /// <summary>Moves every save file of the player into the archive directory, as full files, and forgets the versions.</summary>
    public async Task Archive(long playerId, string archiveDir)
    {
        await files.PerStoreLock.WaitAsync();
        try
        {
            Directory.CreateDirectory(archiveDir);
            foreach (var version in await db.SaveVersions.Where(v => v.PlayerId == playerId).ToListAsync())
            {
                MoveOut(version, archiveDir);
                db.SaveVersions.Remove(version);
            }
            files.PruneBases(playerId);
            await using var tx = await db.Database.BeginTransactionAsync();
            await InfluenceLifecycle.RevokePlayers(db, [playerId]);
            var influenceWorld = await InfluenceLifecycle.Advance(db);
            // A fresh colony starts at tick 0: the transfer stamp and the redelivery marks of the old one must not follow it.
            await db.Players.Where(p => p.Id == playerId).ExecuteUpdateAsync(s => s
                .SetProperty(p => p.LastOutgoingTick, 0L)
                .SetProperty(p => p.SaveGeneration, p => p.SaveGeneration + 1));
            await db.Parcels.Where(p => p.ToPlayerId == playerId && p.DeliveredAt != null).ExecuteUpdateAsync(s => s.SetProperty(p => p.DeliveredTick, 0L));
            await db.SaveChangesAsync();
            await tx.CommitAsync();
            if (influenceWorld is not null) influence.Notify(influenceWorld);
        }
        finally
        {
            files.PerStoreLock.Release();
        }
    }

    /// <summary>No row or manifest speaks for these files afterwards, so each must stand alone; a delta that cannot goes along with its base.</summary>
    private void MoveOut(SaveVersion version, string archiveDir)
    {
        string target = Path.Combine(archiveDir, Path.GetFileName(PathOf(version)));
        switch (files.Locate(version.PlayerId, version.Number))
        {
            case SaveForm.Full full:
                File.Move(full.Path, target, overwrite: true);
                break;
            case SaveForm.Delta delta:
                try
                {
                    files.Rebuild(delta, target);
                }
                catch (InvalidDataException ex)
                {
                    log.LogError(ex, "Save v{Version} of player {PlayerId} cannot be rebuilt: archiving its delta and base as they are", version.Number, version.PlayerId);
                    File.Copy(delta.Path, Path.Combine(archiveDir, Path.GetFileName(delta.Path)), overwrite: true);
                    File.Copy(delta.BasePath, Path.Combine(archiveDir, Path.GetFileName(delta.BasePath)), overwrite: true);
                }
                break;
            case SaveForm.Missing:
                foreach (var orphan in files.Deltas(version.PlayerId, version.Number))
                {
                    log.LogError("Save v{Version} of player {PlayerId} lost its base: archiving its delta as it is", version.Number, version.PlayerId);
                    File.Copy(orphan.Path, Path.Combine(archiveDir, Path.GetFileName(orphan.Path)), overwrite: true);
                }
                break;
        }
        files.Delete(version.PlayerId, version.Number);
    }

    public static SaveInfo ToInfo(SaveVersion v) => new()
    {
        Version = v.Number,
        Size = v.Size,
        CreatedAt = Timestamp.FromDateTime(DateTime.SpecifyKind(v.CreatedAt, DateTimeKind.Utc)),
        IsCurrent = v.IsCurrent,
        GameTick = v.GameTick,
        SnapshotId = v.SnapshotId ?? "", SaveWriteEpoch = v.SaveWriteEpoch, RecoveryVersion = v.RecoveryVersion,
    };

    public static SaveRecoveryData RecoveryOf(SaveVersion? version)
    {
        if (version is null || version.RecoveryVersion == 0) return new();
        try
        {
            var result = JsonSerializer.Deserialize<SaveRecoveryData>(version.RecoveryJson ?? "") ?? throw new InvalidDataException("missing snapshot metadata");
            result.Validate();
            if (result.Version != version.RecoveryVersion || result.SnapshotId != version.SnapshotId || result.Generation != version.SaveGeneration || result.WriteEpoch != version.SaveWriteEpoch)
                throw new InvalidDataException("snapshot metadata disagrees with the version");
            return result;
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        { throw new RpcException(new Status(StatusCode.Unavailable, "snapshot recovery metadata is unavailable")); }
    }

    private string PathOf(SaveVersion v) => files.FullPath(v.PlayerId, v.Number);

    private async Task ApplyRetention(long playerId)
    {
        var all = await db.SaveVersions.Where(v => v.PlayerId == playerId).ToListAsync();
        var protectedIds = await db.OutpostSnapshotLinks.Where(l => l.PlayerId == playerId).Select(l => l.SnapshotId).ToListAsync();
        foreach (var old in SaveRetention.Select(all, DateTime.UtcNow).Where(v => v.SnapshotId is null || !protectedIds.Contains(v.SnapshotId)))
        {
            files.Delete(playerId, old.Number);
            db.SaveVersions.Remove(old);
        }
        await db.SaveChangesAsync();
        files.PruneBases(playerId);
    }
}
