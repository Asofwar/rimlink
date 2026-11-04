using System.Security.Cryptography;
using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Saves;
using Rimlink.Server.Storage.Entities;

namespace Rimlink.Server.Application.Outposts;

public sealed partial class OutpostLifecycle
{
    internal async Task<OutpostLifecycleOperation> PrepareArchiveHeldAsync(OutpostLease lease, long playerId,
        IReadOnlyList<SaveVersion> versions, DateTime cutUtc, string actor, CancellationToken ct)
    {
        lease.Require(authority);
        if (!lease.CoversOwner(playerId) || lease.LifecycleOperationId is not null) throw new InvalidOperationException("lifecycle-owned-barrier-required");
        var cut = clock.Sample(); cutUtc = cut.Utc;
        var head = await db.OutpostAuthority.AsNoTracking().SingleAsync(ct);
        var ownerScopes = new List<LifecycleOwner>();
        foreach (long owner in lease.OwnerIds)
        {
            var player = await db.Players.AsNoTracking().SingleAsync(p => p.Id == owner, ct);
            ownerScopes.Add(new(new OutpostScopeMessage { WorldInstanceId = head.WorldId, OwnerPlayerId = owner,
                SaveGeneration = player.SaveGeneration, SaveWriteEpoch = player.SaveWriteEpoch, RestoreBoundary = head.RestoreBoundary }.ToByteArray()));
        }
        string id = Guid.NewGuid().ToString("D");
        var outposts = await db.Outposts.AsNoTracking().Where(o => o.PlayerId == playerId && o.WorldId == head.WorldId && o.OccupiesSlot)
            .OrderBy(o => o.Id).Select(o => new LifecycleOutpost(o.Id, o.State)).ToArrayAsync(ct);
        if (outposts.Any(o => o.State is not ("Active" or "SafetyHold"))) throw new OutpostEvidenceUnavailableException("lifecycle-archive-state-invalid");
        var evidenceFiles = new Dictionary<string, LifecycleFile>(StringComparer.Ordinal);
        foreach (var v in versions)
        {
            _ = files.ReadEvidence(v);
            IEnumerable<string> paths = files.Locate(playerId, v.Number) switch
            {
                SaveForm.Full f => [f.Path], SaveForm.Delta d => [d.Path, d.BasePath],
                _ => throw new OutpostEvidenceUnavailableException("lifecycle-source-bytes-missing")
            };
            paths = paths.Concat(files.Deltas(playerId, v.Number).SelectMany(d => new[] { d.Path, d.BasePath }));
            foreach (string path in paths.Distinct())
            {
                using var stream = File.OpenRead(path);
                string name = Path.GetFileName(path);
                if (stream.Length <= 0 || stream.Length > Rimlink.Protocol.Limits.MaxSaveBytes) throw new OutpostEvidenceUnavailableException("lifecycle-source-size-limit");
                evidenceFiles[name] = new(playerId, name, stream.Length, SHA256.HashData(stream));
            }
        }
        var intent = new LifecycleIntent(1, id, "Archive", head.WorldId, head.RestoreBoundary, actor,
            head.HeadId, head.HeadDigest, lease.OwnerIds.ToArray(), ownerScopes.ToArray(), Path.Combine("archive", "outpost-evidence", id),
            cutUtc, cut, outposts, versions.Select(v => new LifecycleVersion(head.WorldId, playerId,
                new(v.Number, v.GameTick, v.Size, Convert.ToHexString(v.Sha256), v.CreatedAt, v.IsCurrent, v.DevMode,
                    v.WealthTotal, v.WealthItems, v.SnapshotId, v.SaveGeneration, v.SaveWriteEpoch, v.RecoveryVersion,
                    v.RawSha256, v.RecoveryJson))).ToArray(), evidenceFiles.Values.OrderBy(f => f.Name, StringComparer.Ordinal).ToArray(), playerId);
        byte[] encoded = OutpostLifecycleEvidence.Encode(intent);
        var row = new OutpostLifecycleOperation { Id = id, WorldId = head.WorldId, Actor = actor, Kind = "Archive", RequestId = id,
            RestoreBoundary = head.RestoreBoundary, Phase = "Prepared", Intent = encoded, IntentDigest = SHA256.HashData(encoded), CreatedAt = cutUtc };
        lease.LifecycleOperationId = id;
        await PhaseHeldAsync(lease, row, null, "Prepared", 0, false, async token =>
        {
            db.OutpostLifecycleOperations.Add(row);
            await StageArchiveHeldAsync(lease, playerId, cutUtc, token);
        }, ct);
        return row;
    }

    public async Task StageArchiveHeldAsync(OutpostLease lease, long playerId, DateTime cutUtc, CancellationToken ct)
    {
        if (!lease.CoversOwner(playerId) || lease.LifecycleOperationId is null || db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("lifecycle-owned-transaction-required");
        var operation = db.OutpostLifecycleOperations.Local.SingleOrDefault(r => r.Id == lease.LifecycleOperationId)
            ?? await db.OutpostLifecycleOperations.SingleAsync(r => r.Id == lease.LifecycleOperationId, ct);
        var now = OutpostLifecycleEvidence.Intent(operation).Cut;
        if (now.Utc != cutUtc) throw new InvalidOperationException("lifecycle-cut-clock-mismatch");
        var rows = await db.Outposts.AsNoTracking().Where(o => o.PlayerId == playerId && o.OccupiesSlot).ToListAsync(ct);
        foreach (var row in rows)
        {
            await assignments.SettleAsync(row, now, ct);
            await db.Outposts.Where(o => o.Id == row.Id).ExecuteUpdateAsync(s => s.SetProperty(o => o.State, "Archived").SetProperty(o => o.Revision, o => o.Revision + 1), ct);
        }
    }

    internal async Task RetainArchiveEvidenceHeldAsync(OutpostLease lease, OutpostLifecycleOperation row, CancellationToken ct)
    {
        var intent = OutpostLifecycleEvidence.Intent(row);
        string root = files.EvidenceArchiveDirectory(intent.Directory);
        if (Directory.Exists(root)) throw new OutpostEvidenceUnavailableException("lifecycle-evidence-destination-exists");
        Directory.CreateDirectory(root);
        foreach (var file in intent.Files)
        {
            ct.ThrowIfCancellationRequested();
            string destination = Path.Combine(root, file.PlayerId.ToString(System.Globalization.CultureInfo.InvariantCulture), file.Name);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            string source = files.Locate(file.PlayerId, intent.Versions.First(v => v.PlayerId == file.PlayerId).Version.Number) switch
            {
                SaveForm.Full full => Path.Combine(Path.GetDirectoryName(full.Path)!, file.Name),
                SaveForm.Delta delta => Path.Combine(Path.GetDirectoryName(delta.Path)!, file.Name),
                _ => throw new OutpostEvidenceUnavailableException("lifecycle-source-bytes-missing")
            };
            File.Copy(source, destination, overwrite: false);
        }
        await new OutpostLifecycleEvidence(db, files).VerifyLocators(intent, ct);
        await PhaseHeldAsync(lease, row, "Prepared", "EvidenceRetained", 0, false, _ => Task.CompletedTask, ct);
    }

    internal Task DestructiveHeldAsync(OutpostLease lease, OutpostLifecycleOperation row, Func<CancellationToken, Task> stage,
        Func<long> archiveId, CancellationToken ct) =>
        PhaseHeldAsync(lease, row, "EvidenceRetained", "Destructive", 0, false, stage, ct, archiveId);

    internal Task CompleteArchiveHeldAsync(OutpostLease lease, OutpostLifecycleOperation row, long archiveId,
        Func<CancellationToken, Task> stage, CancellationToken ct) =>
        PhaseHeldAsync(lease, row, "Destructive", "Completed", archiveId, true, stage, ct);

    internal void SourceCleanupCheckpoint() => Checkpoint?.Invoke("SourceCleanup");

    private async Task PhaseHeldAsync(OutpostLease lease, OutpostLifecycleOperation row, string? expected, string phase,
        long archiveId, bool cleaned, Func<CancellationToken, Task> stage, CancellationToken ct, Func<long>? generatedArchiveId = null)
    {
        var head = await db.OutpostAuthority.AsNoTracking().SingleAsync(ct);
        var old = row.Progress.Length == 0 ? null : OutpostLifecycleEvidence.Decode<LifecycleProgress>(row.Progress);
        byte[] previous = old is null ? [] : (await db.OutpostTransitions.AsNoTracking().SingleAsync(t => t.Id == old.TransitionId, ct)).Digest;
        string transitionId = Guid.NewGuid().ToString("D");
        var intent = OutpostLifecycleEvidence.Intent(row);
        long generation = intent.PlayerId == 0 ? 0 : OutpostScopeMessage.Parser.ParseFrom(intent.Scopes[Array.IndexOf(intent.Owners, intent.PlayerId)].Scope).SaveGeneration;
        var transition = new TransitionIntent(transitionId, head.WorldId, intent.PlayerId, generation,
            head.RestoreBoundary, "Lifecycle", row.Actor, new(head.HeadId, ByteString.CopyFrom(head.HeadDigest)), ByteString.Empty);
        await using var transaction = await authority.BeginLifecycleHeldAsync(lease, transition,
            new(row.Id, expected, row.IntentDigest, phase), ct);
        row.Phase = phase;
        await stage(ct);
        if (generatedArchiveId is not null) archiveId = generatedArchiveId();
        row.Progress = OutpostLifecycleEvidence.Encode(new LifecycleProgress(1, phase, transitionId, old?.TransitionId, previous, archiveId, cleaned));
        if (phase == "Completed") row.Result = OutpostLifecycleEvidence.Encode(new LifecycleResult(1, row.Kind == "Recreate" ? "NoActiveWorld" : row.Kind, archiveId));
        var evidence = new LifecycleTransition(1, row.Id, row.IntentDigest, phase, old?.TransitionId, previous, archiveId, cleaned);
        // Detached copies are used across transactions; attach only the single journal row being advanced.
        if (db.Entry(row).State == EntityState.Detached) db.OutpostLifecycleOperations.Update(row);
        await transaction.CompleteAsync(ByteString.CopyFrom(OutpostLifecycleEvidence.Encode(evidence)), ct);
        Checkpoint?.Invoke(phase);
    }
}
