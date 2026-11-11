using System.Security.Cryptography;
using System.Text.Json;
using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Storage;
using Rimlink.Server.Storage.Entities;

namespace Rimlink.Server.Application.GuildCapture;

internal sealed record GuildSnapshotFamilyMarker(string SnapshotId, byte[] RawHash);

internal static partial class GuildSnapshotGuard
{
    internal const string MarkerKind = "GuildSnapshotFamily";

    internal static async Task<bool> RequiresClientAsync(RimlinkDbContext db, long player, CancellationToken ct) =>
        await db.Set<GuildResourceClaim>().AnyAsync(c => c.RecipientId == player, ct)
            || await db.Set<GuildCaptureOperation>().AnyAsync(o => o.ActorId == player && o.Kind == MarkerKind, ct);

    internal static async Task ValidateProposedAsync(RimlinkDbContext db, long player, OutpostSaveMetadata metadata, CancellationToken ct,
        OutpostSaveMetadata? restoreSource = null)
    {
        if (metadata.GuildMetadata is null)
        {
            if (await RequiresClientAsync(db, player, ct)) throw Invalid("guild-save-family-missing");
            return;
        }
        GuildResourceContracts.ValidateMetadata(metadata.GuildMetadata);
        await ValidateStoredMetadataAsync(db, metadata, ct, restoreSource);
        var accepted = await (from link in db.Set<GuildParcelLink>().AsNoTracking()
            join claim in db.Set<GuildResourceClaim>().AsNoTracking() on link.ClaimId equals claim.Id
            where claim.RecipientId == player && claim.WorldId == metadata.Scope.WorldInstanceId && claim.SaveGeneration == metadata.Scope.SaveGeneration
                && link.AcceptedSnapshotId != null
            select link.ParcelId).ToArrayAsync(ct);
        if (accepted.Any(id => !metadata.GuildMetadata.Receipts.Any(r => r.ParcelId == id))) throw Invalid("guild-delivery-receipt-erased");
    }

    internal static async Task StageFamilyAsync(RimlinkDbContext db, SaveVersion snapshot, OutpostSaveMetadata metadata, CancellationToken ct)
    {
        if (metadata.GuildMetadata is null || await db.Set<GuildCaptureOperation>().AnyAsync(o => o.ActorId == snapshot.PlayerId && o.Kind == MarkerKind, ct)) return;
        string transition = Guid.ParseExact(snapshot.SnapshotId!, "N").ToString("D");
        db.Add(new GuildCaptureOperation { Id = Guid.NewGuid().ToString("D"), ActorId = snapshot.PlayerId, RequestId = transition,
            Kind = MarkerKind, TransitionId = transition, IntentDigest = SHA256.HashData(metadata.ToByteArray()),
            Receipt = JsonSerializer.SerializeToUtf8Bytes(new GuildSnapshotFamilyMarker(snapshot.SnapshotId!, snapshot.RawSha256!)) });
    }

    internal static async Task ValidateMarkerAsync(RimlinkDbContext db, GuildCaptureOperation marker,
        IReadOnlyDictionary<string, OutpostTransition> transitions, CancellationToken ct)
    {
        if (marker.ActorId <= 0 || !Guid.TryParseExact(marker.Id, "D", out _)
            || !transitions.TryGetValue(marker.TransitionId, out var source) || source.Kind != "SnapshotAccepted"
            || source.PlayerId != marker.ActorId || marker.IntentDigest.Length != 32
            || !SHA256.HashData(source.Intent).AsSpan().SequenceEqual(marker.IntentDigest) || marker.Receipt.Length is < 1 or > 1024)
            throw Invalid("guild-family-marker-mismatch");
        GuildSnapshotFamilyMarker? receipt;
        try { receipt = OutpostRestoreEvidence.Decode<GuildSnapshotFamilyMarker>(marker.Receipt); }
        catch (Exception error) when (error is JsonException or InvalidDataException) { throw Invalid("guild-family-marker-invalid"); }
        if (receipt is null || receipt.RawHash is not { Length: 32 } || !Guid.TryParseExact(receipt.SnapshotId, "N", out var id)
            || marker.RequestId != id.ToString("D")) throw Invalid("guild-family-marker-invalid");
        var link = await db.OutpostSnapshotLinks.AsNoTracking().SingleOrDefaultAsync(l => l.PlayerId == marker.ActorId
            && l.SnapshotId == receipt.SnapshotId && l.TransitionId == marker.TransitionId, ct);
        if (link is null || !link.RawSha256.AsSpan().SequenceEqual(receipt.RawHash) || !link.Metadata.AsSpan().SequenceEqual(source.Intent)
            || OutpostSaveMetadata.Parser.ParseFrom(link.Metadata).GuildMetadata is null) throw Invalid("guild-family-marker-source-missing");
    }

    private static OutpostEvidenceUnavailableException Invalid(string reason) => new(reason);
}
