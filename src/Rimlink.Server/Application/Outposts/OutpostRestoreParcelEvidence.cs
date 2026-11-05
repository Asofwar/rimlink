using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using Rimlink.Protocol.V1;
using Microsoft.EntityFrameworkCore;
using Rimlink.Server.Application.Saves;
using Rimlink.Server.Backup;
using Rimlink.Server.Storage;
using Rimlink.Server.Storage.Entities;

namespace Rimlink.Server.Application.Outposts;

internal sealed record RestoreParcelMap(OutpostParcelLink Original, OutpostRestoreAuthorization Authorization);

public sealed partial class OutpostRestoreEvidence
{
    internal static async Task<OutpostDeliveryRestoreAuthorization?> ReadParcelAuthorizationAsync(RimlinkDbContext db, SaveFiles files,
        OutpostParcelLink original, OutpostScopeMessage? scope, CancellationToken ct)
    {
        if (scope is null)
        {
            var head = await db.OutpostAuthority.AsNoTracking().SingleAsync(ct);
            var player = await db.Players.AsNoTracking().SingleAsync(p => p.Id == original.PlayerId, ct);
            scope = new() { WorldInstanceId = head.WorldId, OwnerPlayerId = player.Id, SaveGeneration = player.SaveGeneration,
                SaveWriteEpoch = player.SaveWriteEpoch, RestoreBoundary = head.RestoreBoundary };
        }
        if (original.RestoreBoundary == scope.RestoreBoundary) return null;
        var authorization = await db.OutpostRestoreAuthorizations.AsNoTracking().SingleOrDefaultAsync(a => a.ParcelId == original.ParcelId
            && a.RestoreBoundary == scope.RestoreBoundary, ct);
        if (authorization is null) return null; // An unresolved/retired parcel has no materialization grant.
        var root = await db.OutpostTransitions.AsNoTracking().SingleOrDefaultAsync(t => t.Id == authorization.TransitionId, ct);
        var operation = await db.OutpostLifecycleOperations.AsNoTracking().SingleOrDefaultAsync(o => o.Id == authorization.TransitionId, ct);
        if (root is null || operation is null || operation.Kind != "BulkRestore" || operation.Phase != "Completed"
            || !operation.Intent.AsSpan().SequenceEqual(root.Intent) || root.Digest.Length != 32)
            throw Invalid("delivery-restore-transition-missing");
        var plan = Plan(root);
        var map = plan.Parcels.SingleOrDefault(m => m.Original.ParcelId == original.ParcelId);
        var owner = plan.Players.SingleOrDefault(p => p.PlayerId == original.PlayerId && p.Generation == original.SaveGeneration);
        var comparable = Decode<OutpostParcelLink>(Encode(original)); comparable.AcceptedSnapshotId = null;
        if (map is null || owner?.Canonical is not { } canonical || !Same(map.Authorization, authorization) || !Same(map.Original, comparable)
            || scope.WorldInstanceId != root.WorldId || scope.OwnerPlayerId != owner.PlayerId || scope.SaveGeneration != owner.Generation
            || scope.RestoreBoundary != root.RestoreBoundary)
            throw Invalid("delivery-restore-authorization-mismatch");
        var restored = scope.Clone(); restored.SaveWriteEpoch = owner.WriteEpoch;
        await new OutpostRestoreEvidence(db, files).ValidateDeliveryEpochPathAsync(restored, scope, ct);
        var originalPayload = OutpostParcelPayload.Parser.ParseFrom(original.Payload);
        return new() { AuthorizationId = authorization.Id,
            RestoreTransition = new() { TransitionId = root.Id, TransitionDigest = ByteString.CopyFrom(root.Digest) },
            OriginalScope = originalPayload.Scope.Clone(), RestoredScope = scope.Clone(), ParcelId = original.ParcelId,
            PayloadDigest = ByteString.CopyFrom(original.PayloadDigest),
            SelectedSnapshot = new() { SnapshotId = canonical.SnapshotId, RawSha256 = ByteString.CopyFrom(canonical.RawSha256),
                Scope = SaveStore.RecoveryOf(canonical).Outposts.Metadata.Scope.Clone() } };
    }

    internal static async Task<OutpostDeliveryRestoreAuthorization?> ValidateDeliveryRequestAsync(RimlinkDbContext db, SaveFiles files,
        OutpostParcelLink original, OutpostDeliveryRequest request, CancellationToken ct)
    {
        var payload = OutpostParcelPayload.Parser.ParseFrom(original.Payload);
        var authorization = await ReadParcelAuthorizationAsync(db, files, original, request.Scope, ct);
        if (request.ParcelId != original.ParcelId || !request.PayloadDigest.Span.SequenceEqual(original.PayloadDigest)
            || request.CanonicalSnapshot is null || !request.Scope.Equals(request.CanonicalSnapshot.Scope)
            || (authorization is null ? request.RestoreAuthorizationId.Length != 0 || !payload.Scope.Equals(request.Scope)
                : request.RestoreAuthorizationId != authorization.AuthorizationId
                    || request.CanonicalSnapshot.SnapshotId == authorization.SelectedSnapshot.SnapshotId))
            throw Invalid("delivery-restore-request-mismatch");
        return authorization;
    }

    private async Task ValidateDeliveryEpochPathAsync(OutpostScopeMessage restored, OutpostScopeMessage current, CancellationToken ct)
    {
        var cursor = current.Clone();
        for (int depth = 0; !cursor.Equals(restored); depth++)
        {
            if (depth >= Rimlink.Protocol.OutpostSourceGraph.MaxDepth || cursor.SaveWriteEpoch <= restored.SaveWriteEpoch)
                throw Invalid("delivery-restore-epoch-path-missing");
            var roots = await SourceRollbackRootsAsync(cursor, ct);
            if (roots.Length != 1) throw Invalid("delivery-restore-epoch-path-missing-or-ambiguous");
            // Validate the actual immutable operation, transition and both source proofs, not merely an epoch range.
            var (_, intent, _) = await SourceRollbackAsync(roots[0].Id, ct);
            cursor = OutpostScopeMessage.Parser.ParseFrom(intent.Scopes[0].Scope);
            var expected = restored.Clone(); expected.SaveWriteEpoch = cursor.SaveWriteEpoch;
            if (!cursor.Equals(expected)) throw Invalid("delivery-restore-epoch-path-namespace");
        }
    }

    private static string ParcelAuthorizationId(string restoreId, long parcelId) => new Guid(SHA256.HashData(
        Encoding.UTF8.GetBytes("restore-parcel:v1:" + restoreId + ":" + parcelId.ToString(System.Globalization.CultureInfo.InvariantCulture))).AsSpan(0, 16)).ToString("D");

    private static async Task<RestoreParcelMap[]> CreateParcelMapsAsync(RimlinkDbContext source, RestoreIntent journal,
        OutpostBackupManifest cut, IReadOnlyList<RestorePlayerMap> owners, CancellationToken ct)
    {
        var pending = await source.OutpostParcelLinks.AsNoTracking().Where(p => p.WorldId == cut.Authority.WorldId && p.AcceptedSnapshotId == null)
            .OrderBy(p => p.ParcelId).Take(10001).ToListAsync(ct);
        if (pending.Count > 10000) throw Invalid("restore-parcel-map-limit");
        var maps = new List<RestoreParcelMap>();
        foreach (var original in pending)
        {
            var owner = owners.SingleOrDefault(p => p.PlayerId == original.PlayerId && p.Generation == original.SaveGeneration);
            if (owner?.Canonical is not { } canonical) continue; // No current physical source: affected delivery stays fenced.
            if (original.RestoreBoundary != cut.Authority.RestoreBoundary)
            {
                var previous = await source.OutpostRestoreAuthorizations.AsNoTracking().SingleOrDefaultAsync(a => a.ParcelId == original.ParcelId
                    && a.RestoreBoundary == cut.Authority.RestoreBoundary, ct);
                if (previous is null) continue; // Retired or unresolved mail is not resurrected by a new boundary.
                var root = await source.OutpostTransitions.AsNoTracking().SingleOrDefaultAsync(t => t.Id == previous.TransitionId, ct);
                if (root is null || root.RestoreBoundary != cut.Authority.RestoreBoundary
                    || !Plan(root).Parcels.Any(m => Same(m.Authorization, previous) && Same(m.Original, original)))
                    throw Invalid("restore-previous-parcel-authorization-missing");
            }
            var parcel = await source.Parcels.AsNoTracking().SingleOrDefaultAsync(p => p.Id == original.ParcelId, ct);
            if (parcel is null || parcel.ToPlayerId != owner.PlayerId || original.PayloadDigest.Length != 32
                || !SHA256.HashData(original.Payload).AsSpan().SequenceEqual(original.PayloadDigest))
                throw Invalid("restore-original-parcel-mismatch");
            if (parcel.DeliveredAt is not null || parcel.Decision == ParcelDecisionDb.Refused
                || await source.OutpostDeliveryAttempts.AnyAsync(a => a.ParcelId == original.ParcelId && (a.Active || a.State != "Accepted"), ct))
                continue; // Started/unknown outcomes retain all evidence but receive no new materialization grant.
            if (canonical.RawSha256?.Length != 32 || !Guid.TryParseExact(canonical.SnapshotId, "N", out var snapshotId) || snapshotId == Guid.Empty
                || !await source.OutpostSnapshotLinks.AnyAsync(l => l.WorldId == original.WorldId && l.PlayerId == owner.PlayerId
                    && l.SaveGeneration == owner.Generation && l.SnapshotId == canonical.SnapshotId && l.RawSha256 == canonical.RawSha256, ct))
                throw Invalid("restore-parcel-canonical-proof-missing");
            maps.Add(new(original, new OutpostRestoreAuthorization
            {
                Id = ParcelAuthorizationId(journal.Selection.RestoreId, original.ParcelId), ParcelId = original.ParcelId,
                RestoreBoundary = journal.Selection.TargetBoundary, TransitionId = journal.Selection.RestoreId,
                PayloadDigest = original.PayloadDigest.ToArray(), CanonicalSnapshotId = canonical.SnapshotId,
                CanonicalSnapshotHash = canonical.RawSha256.ToArray()
            }));
        }
        return maps.ToArray();
    }

    private async Task ValidateParcelMapsStoredAsync(OutpostTransition root, RestoreMainPlan plan,
        IReadOnlyDictionary<string, OutpostRestoreAuthorization> authorizations, HashSet<string> referenced, CancellationToken ct)
    {
        foreach (var map in plan.Parcels)
        {
            var original = map.Original; var authorization = map.Authorization;
            var owner = plan.Players.SingleOrDefault(p => p.PlayerId == original.PlayerId && p.Generation == original.SaveGeneration);
            if (original.ParcelId <= 0 || original.AcceptedSnapshotId is not null || original.WorldId != root.WorldId
                || owner?.Canonical is not { } canonical || authorization.Id != ParcelAuthorizationId(root.Id, original.ParcelId)
                || authorization.ParcelId != original.ParcelId || authorization.RestoreBoundary != root.RestoreBoundary || authorization.TransitionId != root.Id
                || !authorization.PayloadDigest.AsSpan().SequenceEqual(original.PayloadDigest)
                || authorization.CanonicalSnapshotId != canonical.SnapshotId || !authorization.CanonicalSnapshotHash.AsSpan().SequenceEqual(canonical.RawSha256)
                || !referenced.Add(authorization.Id) || !authorizations.TryGetValue(authorization.Id, out var stored) || !Same(stored, authorization))
                throw Invalid("restore-parcel-authorization-mismatch");
            var current = await db.OutpostParcelLinks.AsNoTracking().SingleOrDefaultAsync(p => p.ParcelId == original.ParcelId, ct);
            if (current is null) throw Invalid("restore-original-parcel-missing");
            current.AcceptedSnapshotId = null; // A later exact receipt may close this same immutable payload, never replace it.
            if (!Same(current, original)) throw Invalid("restore-original-parcel-changed");
        }
    }
}
