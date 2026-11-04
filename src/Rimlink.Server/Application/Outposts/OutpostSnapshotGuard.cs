using System.Security.Cryptography;
using System.Text.Json;
using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Saves;
using Rimlink.Server.Storage;
using Rimlink.Server.Storage.Entities;

namespace Rimlink.Server.Application.Outposts;

public sealed record SnapshotDecision(bool Protected, bool Replay = false);

/// <summary>Exact immutable evidence only. Callers own A → O → S (startup owns A → S).</summary>
public sealed partial class OutpostSnapshotGuard(RimlinkDbContext db, SaveFiles files)
{
    internal static readonly string[] SnapshotIndependentKinds = ["Initialize", "Production", "StageManifest", "ConfirmManifest"];
    public const int MaxSnapshots = 10_000; // No history GC: refuse new evidence before any pawn can be removed.
    public const long MaxRetainedMetadataBytes = 256L * 1024 * 1024;

    public async Task<bool> RequiresProtectionAsync(long playerId, CancellationToken ct) =>
        await db.OutpostSnapshotLinks.AnyAsync(l => l.PlayerId == playerId, ct)
        || await db.PawnCustody.AnyAsync(p => p.PlayerId == playerId, ct);

    public async Task<SnapshotDecision> ValidateHeldAsync(OutpostLease lease, long playerId, SaveHeader header,
        OutpostRecoveryData recovery, ReadOnlyMemory<byte> rawHash, CancellationToken ct)
    {
        RequireOwner(lease, playerId);
        try { recovery.Validate(); }
        catch (InvalidDataException) { throw Unavailable("snapshot-metadata-invalid"); }
        bool history = await db.OutpostSnapshotLinks.AnyAsync(l => l.PlayerId == playerId, ct)
            || await db.PawnCustody.AnyAsync(l => l.PlayerId == playerId, ct);
        if (!recovery.Present)
        {
            if (history) throw Unavailable("snapshot-protected-section-missing");
            return new(false);
        }
        var authority = await db.OutpostAuthority.AsNoTracking().SingleOrDefaultAsync(ct);
        var player = await db.Players.AsNoTracking().SingleOrDefaultAsync(p => p.Id == playerId, ct);
        var scope = recovery.Metadata.Scope;
        if (authority is null || player is null || rawHash.Length != 32 || header.RecoveryVersion != SaveRecoveryData.CurrentVersion
            || !Guid.TryParseExact(header.SnapshotId, "N", out _) || scope.WorldInstanceId != authority.WorldId
            || scope.OwnerPlayerId != playerId || scope.SaveGeneration != header.SaveGeneration
            || scope.SaveWriteEpoch != header.SaveWriteEpoch || scope.SaveGeneration != player.SaveGeneration
            || scope.SaveWriteEpoch != player.SaveWriteEpoch || scope.RestoreBoundary != authority.RestoreBoundary)
            throw Unavailable("snapshot-scope-mismatch");
        var repeated = await db.OutpostSnapshotLinks.AsNoTracking().SingleOrDefaultAsync(l => l.WorldId == scope.WorldInstanceId
            && l.PlayerId == playerId && l.SaveGeneration == scope.SaveGeneration && l.SnapshotId == header.SnapshotId, ct);
        if (repeated is not null)
        {
            if (!repeated.RawSha256.AsSpan().SequenceEqual(rawHash.Span)
                || !repeated.Metadata.AsSpan().SequenceEqual(recovery.Metadata.ToByteArray())) throw Unavailable("snapshot-identity-reused");
            return new(true, true);
        }
        if (await db.OutpostSnapshotLinks.CountAsync(ct) >= MaxSnapshots
            || await db.OutpostSnapshotLinks.SumAsync(l => (long)l.Metadata.Length, ct) + recovery.Metadata.CalculateSize() > MaxRetainedMetadataBytes)
            throw Unavailable("snapshot-history-limit");
        await ValidateCausalityAsync(recovery.Metadata, ct);
        await ValidateBranchAsync(recovery.Metadata, ct);
        return new(true);
    }

    public async Task StageAcceptedHeldAsync(OutpostLease lease, SaveVersion snapshot, OutpostRecoveryData recovery, CancellationToken ct)
    {
        RequireOwner(lease, snapshot.PlayerId);
        if (db.Database.CurrentTransaction is null) throw new InvalidOperationException("snapshot-acceptance-transaction-required");
        recovery.Validate();
        if (!recovery.Present || snapshot.RawSha256?.Length != 32 || snapshot.SnapshotId is null) throw Unavailable("snapshot-acceptance-invalid");
        var scope = recovery.Metadata.Scope;
        string transition = Guid.ParseExact(snapshot.SnapshotId, "N").ToString("D");
        db.OutpostSnapshotLinks.Add(new OutpostSnapshotLink
        {
            WorldId = scope.WorldInstanceId, PlayerId = snapshot.PlayerId, SaveGeneration = snapshot.SaveGeneration,
            SaveWriteEpoch = snapshot.SaveWriteEpoch, SnapshotId = snapshot.SnapshotId, RestoreBoundary = scope.RestoreBoundary,
            RawSha256 = snapshot.RawSha256.ToArray(), MetadataVersion = recovery.Metadata.SchemaVersion,
            Metadata = recovery.Metadata.ToByteArray(), FrontierDigest = FrontierDigest(recovery.Metadata), TransitionId = transition
        });
        foreach (var envelope in recovery.Metadata.Registry.Where(e => !e.NonAuthoritativeMirror))
        {
            var custody = await db.PawnCustody.SingleOrDefaultAsync(p => p.Id == envelope.PawnCustodyId, ct);
            if (custody is null)
                db.PawnCustody.Add(new PawnCustodyRecord { Id = envelope.PawnCustodyId, WorldId = scope.WorldInstanceId,
                    PlayerId = scope.OwnerPlayerId, SaveGeneration = scope.SaveGeneration, RestoreBoundary = scope.RestoreBoundary,
                    LocationKind = "Home", LocationId = envelope.PawnReferenceToken,
                    AssignmentId = string.IsNullOrEmpty(envelope.AssignmentId) ? null : envelope.AssignmentId,
                    FrontierTransitionId = transition, Tombstone = envelope.Tombstone, Revision = 1 });
            else
            {
                await db.Entry(custody).ReloadAsync(ct);
                if (custody.LocationKind == "Home" && envelope.Tombstone && !custody.Tombstone)
                { custody.Tombstone = true; custody.FrontierTransitionId = transition; custody.Revision++; }
            }
        }
        await StageAdmissionsAsync(recovery.Metadata, transition, ct);
        await StageDeliveriesAsync(snapshot, recovery.Metadata, transition, ct);
    }

    private async Task StageDeliveriesAsync(SaveVersion snapshot, OutpostSaveMetadata metadata, string transitionId, CancellationToken ct)
    {
        foreach (var receipt in metadata.Receipts.Where(r => r.DeliveryAttemptId.Length > 0))
        {
            var attempt = await db.OutpostDeliveryAttempts.SingleAsync(a => a.Id == receipt.DeliveryAttemptId, ct);
            var link = await db.OutpostParcelLinks.SingleAsync(p => p.ParcelId == attempt.ParcelId, ct);
            if (link.AcceptedSnapshotId is not null) continue; // A later save carries the immutable first acceptance, not a second import.
            var parcel = await db.Parcels.SingleAsync(p => p.Id == attempt.ParcelId, ct);
            attempt.Active = false; attempt.State = "Accepted"; link.AcceptedSnapshotId = snapshot.SnapshotId;
            parcel.DeliveredAt = snapshot.CreatedAt; parcel.DeliveredTick = snapshot.GameTick;
            parcel.DeliveryRecoveryVersion = snapshot.RecoveryVersion; parcel.DeliveredGeneration = snapshot.SaveGeneration;
            foreach (string id in receipt.PawnCustodyIds)
            {
                var envelope = metadata.Registry.Single(e => e.PawnCustodyId == id && !e.NonAuthoritativeMirror);
                var custody = await db.PawnCustody.SingleAsync(p => p.Id == id, ct);
                await db.Entry(custody).ReloadAsync(ct);
                custody.LocationKind = "Home"; custody.LocationId = envelope.PawnReferenceToken; custody.Tombstone = false;
                custody.FrontierTransitionId = transitionId; custody.Revision++;
            }
        }
    }

    public Task ValidateStoredEvidenceHeldAsync(OutpostLease lease, CancellationToken ct)
    {
        _ = lease.OwnerIds; // Checks disposal; global A/S covers all retained history, not just these owners.
        return ValidateStoredEvidenceCoreAsync(ct);
    }

    // Used only from authority's already-held startup/commit barrier, without reacquiring any lock or transaction.
    internal async Task ValidateStoredEvidenceCoreAsync(CancellationToken ct, OutpostTransition? staged = null)
    {
        if (await db.OutpostSnapshotLinks.SumAsync(l => (long)l.Metadata.Length, ct) > MaxRetainedMetadataBytes
            || await db.OutpostSnapshotLinks.AnyAsync(l => l.Metadata.Length > OutpostRecoveryData.MaxMetadataBytes, ct))
            throw Unavailable("snapshot-history-limit");
        var links = await db.OutpostSnapshotLinks.AsNoTracking().Take(MaxSnapshots + 1).ToListAsync(ct);
        if (links.Count > MaxSnapshots) throw Unavailable("snapshot-history-limit");
        var transitions = await db.OutpostTransitions.AsNoTracking().ToDictionaryAsync(t => t.Id, ct);
        if (staged is not null) transitions.Add(staged.Id, staged);
        var registrations = new HashSet<(string World, long Player, long Generation, string Custody)>();
        foreach (var link in links)
        {
            try
            {
                var (version, archive) = await ResolveAsync(link, ct);
                byte[] bytes = files.ReadEvidence(version, archive);
                var recovered = SaveRecoveryData.ReadXml(new MemoryStream(bytes, writable: false), out byte[] rawHash);
                var metadata = recovered.Outposts.Metadata;
                if (link.RawSha256.Length != 32 || version.RawSha256?.Length != 32
                    || !rawHash.AsSpan().SequenceEqual(link.RawSha256) || !rawHash.AsSpan().SequenceEqual(version.RawSha256)
                    || version.RecoveryVersion != recovered.Version || version.SnapshotId != recovered.SnapshotId
                    || recovered.SnapshotId != link.SnapshotId || recovered.Generation != link.SaveGeneration
                    || recovered.WriteEpoch != link.SaveWriteEpoch || version.SaveGeneration != link.SaveGeneration
                    || version.SaveWriteEpoch != link.SaveWriteEpoch || !recovered.Outposts.Present
                    || metadata.SchemaVersion != link.MetadataVersion || !metadata.ToByteArray().AsSpan().SequenceEqual(link.Metadata)
                    || !FrontierDigest(metadata).AsSpan().SequenceEqual(link.FrontierDigest)
                    || metadata.Scope.WorldInstanceId != link.WorldId || metadata.Scope.OwnerPlayerId != link.PlayerId
                    || metadata.Scope.SaveGeneration != link.SaveGeneration || metadata.Scope.SaveWriteEpoch != link.SaveWriteEpoch
                    || metadata.Scope.RestoreBoundary != link.RestoreBoundary
                    || !transitions.TryGetValue(link.TransitionId, out var transition) || transition.WorldId != link.WorldId
                    || transition.PlayerId != link.PlayerId || transition.SaveGeneration != link.SaveGeneration
                    || transition.RestoreBoundary != link.RestoreBoundary || transition.Kind != "SnapshotAccepted"
                    || transition.Id != Guid.ParseExact(link.SnapshotId, "N").ToString("D")
                    || !transition.Intent.AsSpan().SequenceEqual(link.Metadata)) throw Unavailable("snapshot-stored-proof-mismatch");
                foreach (var entry in metadata.Registry.Where(e => !e.NonAuthoritativeMirror))
                    registrations.Add((link.WorldId, link.PlayerId, link.SaveGeneration, entry.PawnCustodyId));
                // RecoveryJson is a projection, not a substitute for bytes; when present it must agree as well.
                var projected = JsonSerializer.Deserialize<SaveRecoveryData>(version.RecoveryJson ?? "") ?? throw new InvalidDataException();
                if (projected.SnapshotId != recovered.SnapshotId || projected.Generation != recovered.Generation
                    || projected.WriteEpoch != recovered.WriteEpoch || !projected.Outposts.Metadata.Equals(metadata))
                    throw Unavailable("snapshot-json-proof-mismatch");
                foreach (var frontier in metadata.Frontier)
                    if (!transitions.TryGetValue(frontier.TransitionId, out var entry) || entry.WorldId != link.WorldId
                        || !entry.Digest.AsSpan().SequenceEqual(frontier.TransitionDigest.Span)
                        || (entry.PredecessorId ?? "") != frontier.PredecessorId) throw Unavailable("snapshot-frontier-mismatch");
                foreach (var receipt in metadata.Receipts)
                {
                    if (!transitions.TryGetValue(receipt.TransitionId, out var entry) || entry.WorldId != link.WorldId
                        || entry.PlayerId != link.PlayerId || entry.SaveGeneration != link.SaveGeneration
                        || entry.RestoreBoundary != link.RestoreBoundary
                        || !entry.Digest.AsSpan().SequenceEqual(receipt.TransitionDigest.Span)
                        || (entry.PredecessorId ?? "") != receipt.PredecessorId
                        || !metadata.Frontier.Any(f => f.TransitionId == receipt.TransitionId)) throw Unavailable("snapshot-receipt-mismatch");
                    await ValidateReceiptAsync(metadata, receipt, entry, ct);
                }
            }
            catch (Exception error) when (error is IOException or InvalidDataException or JsonException or System.Xml.XmlException
                or FormatException or ZstdSharp.ZstdException)
            { throw Unavailable("snapshot-stored-bytes-unavailable"); }
        }
        // A missing link table never proves an unrelated projection has evidence.
        foreach (var custody in await db.PawnCustody.AsNoTracking().ToListAsync(ct))
            if (!registrations.Contains((custody.WorldId, custody.PlayerId, custody.SaveGeneration, custody.Id))
                || !transitions.TryGetValue(custody.FrontierTransitionId, out var source) || source.WorldId != custody.WorldId
                || source.PlayerId != custody.PlayerId || source.SaveGeneration != custody.SaveGeneration
                || source.RestoreBoundary != custody.RestoreBoundary)
                throw Unavailable("required-snapshot-evidence-unavailable");
        foreach (var transition in transitions.Values.Where(t => !SnapshotIndependentKinds.Contains(t.Kind)))
            if (!links.Any(l => l.WorldId == transition.WorldId && l.PlayerId == transition.PlayerId && l.SaveGeneration == transition.SaveGeneration))
                throw Unavailable("required-snapshot-evidence-unavailable");
        foreach (var parcel in await db.OutpostParcelLinks.AsNoTracking().ToListAsync(ct))
        {
            if (!links.Any(l => l.WorldId == parcel.WorldId && l.PlayerId == parcel.PlayerId && l.SaveGeneration == parcel.SaveGeneration)
                || !transitions.TryGetValue(parcel.TransitionId, out var source) || source.WorldId != parcel.WorldId
                || source.PlayerId != parcel.PlayerId || source.SaveGeneration != parcel.SaveGeneration
                || source.RestoreBoundary != parcel.RestoreBoundary || parcel.Payload.Length > OutpostRecoveryData.MaxMetadataBytes
                || parcel.PayloadDigest.Length != 32 || !SHA256.HashData(parcel.Payload).AsSpan().SequenceEqual(parcel.PayloadDigest)
                || !await db.Parcels.AnyAsync(p => p.Id == parcel.ParcelId && p.ToPlayerId == parcel.PlayerId, ct))
                throw Unavailable("required-parcel-evidence-unavailable");
        }
        foreach (var attempt in await db.OutpostDeliveryAttempts.AsNoTracking().ToListAsync(ct))
        {
            if (!links.Any(l => l.WorldId == attempt.WorldId && l.PlayerId == attempt.PlayerId && l.SaveGeneration == attempt.SaveGeneration
                    && l.SnapshotId == attempt.CanonicalSnapshotId && l.SaveWriteEpoch == attempt.SaveWriteEpoch
                    && l.RestoreBoundary == attempt.RestoreBoundary && l.RawSha256.AsSpan().SequenceEqual(attempt.CanonicalSnapshotHash))
                || !transitions.TryGetValue(attempt.TransitionId, out var source) || source.Kind != "BeginDelivery"
                || source.WorldId != attempt.WorldId || source.PlayerId != attempt.PlayerId || source.SaveGeneration != attempt.SaveGeneration
                || source.RestoreBoundary != attempt.RestoreBoundary || attempt.IntentDigest.Length != 32
                || !await db.OutpostParcelLinks.AnyAsync(p => p.ParcelId == attempt.ParcelId && p.PlayerId == attempt.PlayerId, ct))
                throw Unavailable("required-delivery-evidence-unavailable");
        }
        await ValidateAdmissionStoredAsync(transitions, links, ct);
        // Lifecycle/restore codecs are introduced by their own later tasks, never inferred from an unrelated link.
        if (await db.OutpostRestoreAuthorizations.AnyAsync(ct) || await db.OutpostLifecycleOperations.AnyAsync(ct))
            throw Unavailable("required-snapshot-evidence-unavailable");
    }

    internal static byte[] FrontierDigest(OutpostSaveMetadata metadata)
    {
        var frontier = new OutpostSaveMetadata();
        frontier.Frontier.Add(metadata.Frontier);
        return SHA256.HashData(frontier.ToByteArray());
    }

    private async Task ValidateBranchAsync(OutpostSaveMetadata metadata, CancellationToken ct)
    {
        var scope = metadata.Scope;
        var oldLinks = await db.OutpostSnapshotLinks.AsNoTracking().Where(l => l.WorldId == scope.WorldInstanceId
            && l.PlayerId == scope.OwnerPlayerId && l.SaveGeneration == scope.SaveGeneration).ToListAsync(ct);
        foreach (var link in oldLinks)
        {
            var previous = ParseMetadata(link.Metadata);
            if (previous.Receipts.Any(r => !metadata.Receipts.Any(n => n.Equals(r)))
                || previous.Frontier.Any(f => !metadata.Frontier.Any(n => n.Equals(f))))
                throw Unavailable("snapshot-frontier-erased");
        }
        var known = await db.PawnCustody.AsNoTracking().Where(p => p.WorldId == scope.WorldInstanceId
            && p.PlayerId == scope.OwnerPlayerId && p.SaveGeneration == scope.SaveGeneration).ToListAsync(ct);
        var registry = metadata.Registry.Where(e => !e.NonAuthoritativeMirror).ToDictionary(e => e.PawnCustodyId);
        foreach (var custody in known)
        {
            if (!registry.TryGetValue(custody.Id, out var envelope) || envelope.Scope.WorldInstanceId != custody.WorldId
                || envelope.Scope.OwnerPlayerId != custody.PlayerId || envelope.Scope.SaveGeneration != custody.SaveGeneration
                || envelope.Scope.RestoreBoundary != custody.RestoreBoundary
                || envelope.AssignmentId != (custody.AssignmentId ?? "") && !await AdmissionChangesAssignmentAsync(metadata, custody, envelope, ct))
                throw Unavailable("snapshot-registry-lineage-missing");
            string[] deliveryIds = metadata.Receipts.Where(r => r.PawnCustodyIds.Contains(custody.Id)).Select(r => r.DeliveryAttemptId).ToArray();
            bool delivered = custody.LocationKind == "ReturnPending" && await db.OutpostDeliveryAttempts.AsNoTracking().AnyAsync(a =>
                a.Active && a.State == "Started" && a.PlayerId == scope.OwnerPlayerId && a.ParcelId.ToString() == custody.LocationId
                && deliveryIds.Contains(a.Id), ct);
            if (!envelope.Tombstone && (custody.Tombstone || custody.LocationKind is not ("Home" or "Prepared")) && !delivered)
                throw Unavailable("snapshot-server-held-pawn-reappeared");
            if (!envelope.Tombstone && custody.LocationKind == "Home" && envelope.PawnReferenceToken != custody.LocationId)
                throw Unavailable("snapshot-registry-reference-changed");
        }
        foreach (var envelope in registry.Values.Where(e => known.All(p => p.Id != e.PawnCustodyId)))
        {
            if (!envelope.Scope.Equals(scope) || envelope.Tombstone || envelope.AssignmentId.Length != 0
                || await db.PawnCustody.AnyAsync(p => p.Id == envelope.PawnCustodyId, ct))
                throw Unavailable("snapshot-bootstrap-conflict");
        }
    }

    private static OutpostSaveMetadata ParseMetadata(byte[] bytes)
    {
        if (bytes.Length > OutpostRecoveryData.MaxMetadataBytes) throw Unavailable("snapshot-metadata-limit");
        using var input = CodedInputStream.CreateWithLimits(new MemoryStream(bytes, writable: false), OutpostRecoveryData.MaxMetadataBytes, 32);
        var metadata = OutpostSaveMetadata.Parser.ParseFrom(input);
        new OutpostRecoveryData { Metadata = metadata }.Validate();
        return metadata;
    }

    private async Task ValidateCausalityAsync(OutpostSaveMetadata metadata, CancellationToken ct)
    {
        var scope = metadata.Scope;
        var transitions = await db.OutpostTransitions.AsNoTracking().ToDictionaryAsync(t => t.Id, ct);
        foreach (var frontier in metadata.Frontier)
            if (!transitions.TryGetValue(frontier.TransitionId, out var entry) || entry.WorldId != scope.WorldInstanceId
                || (entry.PredecessorId ?? "") != frontier.PredecessorId || !entry.Digest.AsSpan().SequenceEqual(frontier.TransitionDigest.Span))
                throw Unavailable("snapshot-frontier-mismatch");
        foreach (var receipt in metadata.Receipts)
        {
            if (!transitions.TryGetValue(receipt.TransitionId, out var entry) || entry.WorldId != scope.WorldInstanceId
                || entry.PlayerId != scope.OwnerPlayerId || entry.SaveGeneration != scope.SaveGeneration
                || entry.RestoreBoundary != scope.RestoreBoundary || (entry.PredecessorId ?? "") != receipt.PredecessorId
                || !entry.Digest.AsSpan().SequenceEqual(receipt.TransitionDigest.Span)
                || !metadata.Frontier.Any(f => f.TransitionId == receipt.TransitionId))
                throw Unavailable("snapshot-receipt-mismatch");
            await ValidateReceiptAsync(metadata, receipt, entry, ct);
        }
    }

    private async Task ValidateReceiptAsync(OutpostSaveMetadata metadata, OutpostTransitionReceipt receipt,
        OutpostTransition transition, CancellationToken ct)
    {
        if (receipt.DeliveryAttemptId.Length == 0 && transition.Kind is "Prepare" or "CancelFence")
        { await ValidateAdmissionReceiptAsync(metadata, receipt, transition, ct); return; }
        if (receipt.DeliveryAttemptId.Length == 0 || transition.Kind != "BeginDelivery")
            throw Unavailable("snapshot-receipt-kind-unavailable");
        var attempt = await db.OutpostDeliveryAttempts.AsNoTracking().SingleOrDefaultAsync(a => a.Id == receipt.DeliveryAttemptId, ct);
        if (attempt is null || attempt.TransitionId != transition.Id || attempt.WorldId != metadata.Scope.WorldInstanceId
            || attempt.PlayerId != metadata.Scope.OwnerPlayerId || attempt.SaveGeneration != metadata.Scope.SaveGeneration
            || attempt.RestoreBoundary != metadata.Scope.RestoreBoundary || attempt.RequestId != receipt.ClientRequestId
            || !attempt.IntentDigest.AsSpan().SequenceEqual(receipt.IntentDigest.Span) || receipt.CancellationKind != OutpostCancelKind.Unspecified
            || receipt.ParcelIds.Count != 1 || receipt.ParcelIds[0] != attempt.ParcelId
            || receipt.Snapshot.SnapshotId != attempt.CanonicalSnapshotId
            || !attempt.CanonicalSnapshotHash.AsSpan().SequenceEqual(receipt.Snapshot.RawSha256.Span)
            || attempt.State is not ("Started" or "Accepted")) throw Unavailable("snapshot-delivery-receipt-mismatch");
        var canonical = await db.OutpostSnapshotLinks.AsNoTracking().SingleOrDefaultAsync(l => l.WorldId == attempt.WorldId
            && l.PlayerId == attempt.PlayerId && l.SaveGeneration == attempt.SaveGeneration && l.SnapshotId == attempt.CanonicalSnapshotId, ct);
        var parcel = await db.OutpostParcelLinks.AsNoTracking().SingleOrDefaultAsync(p => p.ParcelId == attempt.ParcelId, ct);
        if (canonical is null || parcel is null || parcel.WorldId != attempt.WorldId || parcel.PlayerId != attempt.PlayerId
            || parcel.SaveGeneration != attempt.SaveGeneration || parcel.RestoreBoundary != attempt.RestoreBoundary
            || !canonical.RawSha256.AsSpan().SequenceEqual(attempt.CanonicalSnapshotHash)
            || !ParseMetadata(canonical.Metadata).Scope.Equals(receipt.Snapshot.Scope)
            || parcel.Payload.Length > OutpostRecoveryData.MaxMetadataBytes
            || !SHA256.HashData(parcel.Payload).AsSpan().SequenceEqual(parcel.PayloadDigest)
            || !parcel.PayloadDigest.AsSpan().SequenceEqual(attempt.PayloadDigest)) throw Unavailable("snapshot-delivery-payload-mismatch");
        using var input = CodedInputStream.CreateWithLimits(new MemoryStream(parcel.Payload, writable: false), OutpostRecoveryData.MaxMetadataBytes, 32);
        var payload = OutpostParcelPayload.Parser.ParseFrom(input);
        if (payload.SchemaVersion != 1 || !payload.Scope.Equals(receipt.Snapshot.Scope)
            || !payload.PawnCustodyIds.Order().SequenceEqual(receipt.PawnCustodyIds.Order())
            || !payload.AssignmentIds.Order().SequenceEqual(receipt.AssignmentIds.Order()))
            throw Unavailable("snapshot-delivery-payload-mismatch");
        if (attempt.State == "Started")
        {
            if (!attempt.Active || parcel.AcceptedSnapshotId is not null
                || receipt.PawnCustodyIds.Any(id => !metadata.Registry.Any(e => e.PawnCustodyId == id && !e.Tombstone && !e.NonAuthoritativeMirror)))
                throw Unavailable("snapshot-delivery-not-started");
        }
        else
        {
            var accepted = await db.OutpostSnapshotLinks.AsNoTracking().SingleOrDefaultAsync(l => l.WorldId == attempt.WorldId
                && l.PlayerId == attempt.PlayerId && l.SaveGeneration == attempt.SaveGeneration && l.SnapshotId == parcel.AcceptedSnapshotId, ct);
            if (attempt.Active || accepted is null || !ParseMetadata(accepted.Metadata).Receipts.Any(r => r.Equals(receipt)))
                throw Unavailable("snapshot-delivery-acceptance-missing");
        }
    }

    private async Task<(SaveVersion Version, string? Archive)> ResolveAsync(OutpostSnapshotLink link, CancellationToken ct)
    {
        var live = await db.SaveVersions.AsNoTracking().Where(v => v.PlayerId == link.PlayerId && v.SaveGeneration == link.SaveGeneration
            && v.SnapshotId == link.SnapshotId).ToListAsync(ct);
        if (live.Count == 1) return (live[0], null);
        if (live.Count != 0) throw Unavailable("snapshot-version-ambiguous");
        var archives = await db.ArchivedColonies.AsNoTracking().Where(a => a.PlayerId == link.PlayerId && a.SaveGeneration == link.SaveGeneration).ToListAsync(ct);
        (SaveVersion, string)? selected = null;
        foreach (var row in archives)
        {
            string dir = files.EvidenceArchiveDirectory(row.Dir);
            string path = Path.Combine(dir, SaveArchive.ManifestFile);
            if (!File.Exists(path)) continue;
            if (new FileInfo(path).Length > 64 * 1024 * 1024) throw Unavailable("snapshot-archive-manifest-limit");
            var manifest = JsonSerializer.Deserialize<SaveArchive.Manifest>(await File.ReadAllTextAsync(path, ct), new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (manifest is null || manifest.PlayerId != link.PlayerId) throw Unavailable("snapshot-archive-owner-mismatch");
            foreach (var version in manifest.Versions.Where(v => v.SnapshotId == link.SnapshotId && v.SaveGeneration == link.SaveGeneration))
            {
                if (selected is not null) throw Unavailable("snapshot-archive-ambiguous");
                selected = (new SaveVersion { PlayerId = link.PlayerId, Number = version.Number, GameTick = version.GameTick,
                    Size = version.Size, Sha256 = Convert.FromHexString(version.Sha256), SnapshotId = version.SnapshotId,
                    SaveGeneration = version.SaveGeneration, SaveWriteEpoch = version.SaveWriteEpoch, RecoveryVersion = version.RecoveryVersion,
                    RawSha256 = version.RawSha256, RecoveryJson = version.RecoveryJson }, dir);
            }
        }
        return selected ?? throw Unavailable("snapshot-version-missing");
    }

    private static void RequireOwner(OutpostLease lease, long playerId)
    { if (!lease.CoversOwner(playerId)) throw new InvalidOperationException("outpost-owner-lease-required"); }
    private static OutpostEvidenceUnavailableException Unavailable(string reason) => new(reason);
}
