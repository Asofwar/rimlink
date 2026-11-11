using System.Security.Cryptography;
using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Storage.Entities;

namespace Rimlink.Server.Application.GuildCapture;

internal sealed partial class GuildOutpostService
{
    internal async Task<OutpostManifestPage> ReadDeliveryManifestAsync(long recipient, GuildManifestReadRequest request, CancellationToken ct)
    {
        if (request?.Scope?.PlayerId != recipient || recipient <= 0 || request.CalculateSize() > 4096 || request.ParcelId <= 0
            || request.ManifestDigest.Length != 32 || !Guid.TryParseExact(request.ManifestId, "D", out _) || request.Offset < 0) throw Refused();
        try { GuildResourceContracts.ValidateScope(request.Scope); } catch (InvalidDataException) { throw Refused(); }
        request = request.Clone();
        await using var lease = await authority.AcquireAsync([recipient], ct);
        await RequireScopeAsync(lease, new(request.Scope.WorldInstanceId, request.Scope.RestoreBoundary), ct);
        var player = await db.Players.AsNoTracking().SingleOrDefaultAsync(p => p.Id == recipient, ct);
        if (player is null || player.SaveGeneration != request.Scope.SaveGeneration || player.SaveWriteEpoch != request.Scope.SaveWriteEpoch) throw Refused();
        var link = await db.Set<GuildParcelLink>().AsNoTracking().SingleOrDefaultAsync(p => p.ParcelId == request.ParcelId, ct) ?? throw Refused();
        if (link.Payload.Length > 4096 || !SHA256.HashData(link.Payload).AsSpan().SequenceEqual(link.PayloadDigest)) throw Refused();
        var payload = GuildResourceParcelPayload.Parser.ParseFrom(link.Payload);
        _ = GuildResourceContracts.Validate(payload);
        var claim = await db.Set<GuildResourceClaim>().AsNoTracking().SingleAsync(c => c.Id == link.ClaimId, ct);
        if (!(await GuildRestore.DeliveryScopeAsync(db, claim, link, request.Scope.RestoreBoundary, ct)).Equals(request.Scope)
            || payload.ManifestId != request.ManifestId || !payload.ManifestDigest.Equals(request.ManifestDigest)) throw Refused();
        var manifest = await db.OutpostManifests.AsNoTracking().SingleOrDefaultAsync(m => m.Id == request.ManifestId, ct) ?? throw Refused();
        if (!manifest.Confirmed || manifest.WorldId != request.Scope.WorldInstanceId || manifest.Revision != payload.ValuationRevision
            || manifest.Document.Length is < 1 or > OutpostManifestService.MaxDocumentBytes || !manifest.Digest.AsSpan().SequenceEqual(request.ManifestDigest.Span)
            || !SHA256.HashData(manifest.Document).AsSpan().SequenceEqual(manifest.Digest) || request.Offset >= manifest.Document.Length) throw Refused();
        // A pending parcel retains its original confirmed valuation even after a new manifest is activated.
        return new() { Data = ByteString.CopyFrom(manifest.Document, request.Offset, Math.Min(OutpostManifestService.MaxChunkBytes, manifest.Document.Length - request.Offset)),
            TotalBytes = manifest.Document.Length, Digest = Convert.ToHexStringLower(manifest.Digest) };
    }
}
