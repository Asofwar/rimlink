using System.Security.Cryptography;
using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Rimlink.Server.Storage.Entities;

namespace Rimlink.Server.Application.Outposts;

public sealed partial class OutpostLifecycle
{
    internal async Task<OutpostLifecycleOperation> PrepareRestoreHeldAsync(OutpostLease lease, long playerId,
        long archiveId, string actor, CancellationToken ct)
    {
        lease.Require(authority);
        if (!lease.CoversOwner(playerId) || lease.LifecycleOperationId is not null) throw new InvalidOperationException("lifecycle-owned-barrier-required");
        var previous = await db.OutpostLifecycleOperations.AsNoTracking().Where(r => r.Kind == "Archive" && r.Phase == "Completed").ToListAsync(ct);
        var sources = previous.Where(r => OutpostLifecycleEvidence.Decode<LifecycleProgress>(r.Progress).ArchiveId == archiveId).ToArray();
        if (sources.Length != 1) throw new OutpostEvidenceUnavailableException("lifecycle-restore-source-missing");
        var source = OutpostLifecycleEvidence.Intent(sources[0]);
        if (source.PlayerId != playerId || !source.Owners.All(lease.CoversOwner)) throw new OutpostEvidenceUnavailableException("lifecycle-restore-owner-mismatch");
        var head = await db.OutpostAuthority.AsNoTracking().SingleAsync(ct);
        var scopes = new List<LifecycleOwner>();
        foreach (long owner in lease.OwnerIds)
        {
            var player = await db.Players.AsNoTracking().SingleAsync(p => p.Id == owner, ct);
            scopes.Add(new(new Protocol.V1.OutpostScopeMessage { WorldInstanceId = head.WorldId, OwnerPlayerId = owner,
                SaveGeneration = player.SaveGeneration, SaveWriteEpoch = player.SaveWriteEpoch, RestoreBoundary = head.RestoreBoundary }.ToByteArray()));
        }
        var cut = clock.Sample(); string id = Guid.NewGuid().ToString("D");
        var intent = source with { Id = id, Kind = "Restore", Actor = actor, HeadId = head.HeadId, HeadDigest = head.HeadDigest,
            Owners = lease.OwnerIds.ToArray(), Scopes = scopes.ToArray(), Directory = Path.Combine("archive", "outpost-evidence", id),
            CutUtc = cut.Utc, Cut = cut, Versions = [], Files = [], ArchiveId = archiveId };
        byte[] encoded = OutpostLifecycleEvidence.Encode(intent);
        var row = new OutpostLifecycleOperation { Id = id, WorldId = head.WorldId, Actor = actor, Kind = "Restore", RequestId = id,
            RestoreBoundary = head.RestoreBoundary, Phase = "Prepared", Intent = encoded, IntentDigest = SHA256.HashData(encoded), CreatedAt = cut.Utc };
        lease.LifecycleOperationId = id;
        await PhaseHeldAsync(lease, row, null, "Prepared", archiveId, false, _ => { db.OutpostLifecycleOperations.Add(row); return Task.CompletedTask; }, ct);
        // The original Archive operation already owns and verifies all immutable retained locators.
        await PhaseHeldAsync(lease, row, "Prepared", "EvidenceRetained", archiveId, false, _ => Task.CompletedTask, ct);
        return row;
    }

    public async Task StageRestoreHeldAsync(OutpostLease lease, long playerId, long generation, DateTime resumeUtc, CancellationToken ct)
    {
        if (!lease.CoversOwner(playerId) || lease.LifecycleOperationId is null || db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("lifecycle-owned-transaction-required");
        var operation = db.OutpostLifecycleOperations.Local.SingleOrDefault(r => r.Id == lease.LifecycleOperationId)
            ?? await db.OutpostLifecycleOperations.SingleAsync(r => r.Id == lease.LifecycleOperationId, ct);
        var intent = OutpostLifecycleEvidence.Intent(operation); var now = intent.Cut;
        if (intent.Kind != "Restore" || resumeUtc != now.Utc) throw new InvalidOperationException("lifecycle-resume-clock-mismatch");
        foreach (var old in intent.Outposts)
        {
            var row = await db.Outposts.AsNoTracking().SingleAsync(o => o.Id == old.Id, ct);
            if (row.PlayerId != playerId || row.SaveGeneration != generation || row.State != "Archived" || !row.OccupiesSlot)
                throw new OutpostEvidenceUnavailableException("lifecycle-restore-outpost-mismatch");
            var resumed = OutpostClock.Resume(new(row.CheckpointUtc, row.ClockProcessId, row.ClockTimestamp, row.ClockFrequency, Paused: true), now);
            await db.Outposts.Where(o => o.Id == row.Id).ExecuteUpdateAsync(s => s.SetProperty(o => o.State, old.State)
                .SetProperty(o => o.CheckpointUtc, resumed.Utc).SetProperty(o => o.ClockProcessId, resumed.ProcessId)
                .SetProperty(o => o.ClockTimestamp, resumed.Timestamp).SetProperty(o => o.ClockFrequency, resumed.Frequency)
                .SetProperty(o => o.Revision, o => o.Revision + 1), ct);
        }
    }
}
