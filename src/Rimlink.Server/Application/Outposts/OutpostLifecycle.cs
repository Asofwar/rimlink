using Microsoft.EntityFrameworkCore;
using Rimlink.Protocol.V1;
using Rimlink.Server.Storage;

namespace Rimlink.Server.Application.Outposts;

public sealed record LifecycleDecision(bool Allowed, string Reason, IReadOnlyList<OutpostScopeMessage> Scopes);

/// <summary>Read-only lifecycle admission under the same authority/owner/storage fence as custody mutations.</summary>
public sealed partial class OutpostLifecycle(RimlinkDbContext db, OutpostAuthority authority,
    Saves.SaveFiles files, Saves.SaveArchive archives, OutpostAssignmentService assignments, OutpostClock clock)
{
    internal Action<string>? Checkpoint { get; set; }

    public async Task<LifecycleDecision> CheckResetHeldAsync(OutpostLease lease, IReadOnlyList<long> players, CancellationToken ct)
    {
        lease.Require(authority);
        long[] ordered = players.Distinct().Order().ToArray();
        if (ordered.Length != players.Count || ordered.Any(id => id <= 0 || !lease.CoversOwner(id)))
            throw new InvalidOperationException("lifecycle-complete-owner-lease-required");
        if ((await authority.CheckHeldAsync(lease, ct)).State != OutpostStartupState.Ready)
            return new(false, "lifecycle-authority-not-ready", []);
        var head = await db.OutpostAuthority.AsNoTracking().SingleAsync(ct);
        var scopes = new List<OutpostScopeMessage>();
        foreach (long playerId in ordered)
        {
            var player = await db.Players.AsNoTracking().SingleAsync(p => p.Id == playerId, ct);
            var scope = new OutpostScopeMessage { WorldInstanceId = head.WorldId, OwnerPlayerId = playerId,
                SaveGeneration = player.SaveGeneration, SaveWriteEpoch = player.SaveWriteEpoch, RestoreBoundary = head.RestoreBoundary };
            scopes.Add(scope);
            if (await db.ArchivedColonies.AnyAsync(a => a.PlayerId == playerId && a.SaveGeneration == player.SaveGeneration && a.State == Storage.Entities.ArchiveState.Archived, ct))
                return new(false, "lifecycle-restore-archived-owner-first", scopes);
            if (await db.Outposts.AnyAsync(o => o.PlayerId == playerId && o.WorldId == head.WorldId && o.SaveGeneration == player.SaveGeneration
                && (o.OccupiesSlot || o.State != "Closed"), ct))
                return new(false, "lifecycle-close-outpost-first", scopes);
            if (await db.OutpostOperations.AnyAsync(o => o.PlayerId == playerId && o.WorldId == head.WorldId && o.SaveGeneration == player.SaveGeneration
                && (o.State == "Prepared" || o.State == "CancelFenced"), ct)
                || await db.OutpostReservations.AnyAsync(r => r.PlayerId == playerId && r.WorldId == head.WorldId && r.SaveGeneration == player.SaveGeneration && r.Active, ct))
                return new(false, "lifecycle-staffing-unresolved", scopes);
            if (await db.OutpostParcelLinks.AnyAsync(p => p.PlayerId == playerId && p.WorldId == head.WorldId
                && p.SaveGeneration == player.SaveGeneration && p.AcceptedSnapshotId == null, ct)
                || await db.OutpostDeliveryAttempts.AnyAsync(a => a.PlayerId == playerId && a.WorldId == head.WorldId && a.SaveGeneration == player.SaveGeneration && a.Active, ct)
                || await db.OutpostOrdinaryReturns.AnyAsync(r => r.PlayerId == playerId && r.WorldId == head.WorldId && r.SaveGeneration == player.SaveGeneration && r.AcceptedSnapshotId == null, ct))
                return new(false, "lifecycle-physical-receipt-required", scopes);
            if (await db.PawnCustody.AnyAsync(p => p.PlayerId == playerId && p.WorldId == head.WorldId && p.SaveGeneration == player.SaveGeneration
                && p.LocationKind != "Home" && p.LocationKind != "Dead", ct))
                return new(false, "lifecycle-custody-unresolved", scopes);
        }
        return new(true, "", scopes);
    }

}
