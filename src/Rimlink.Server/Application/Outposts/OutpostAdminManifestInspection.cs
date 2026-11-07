using System.Security.Cryptography;
using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Admin;
using Role = Rimlink.Server.Storage.Entities.Role;

namespace Rimlink.Server.Application.Outposts;

public sealed record OutpostAdminManifestView(string ManifestId, long Revision, byte[] Digest, bool Confirmed, bool Active,
    OutpostManifestBinding Binding, string Surface, int TileCount, string GameVersion, byte[] GameDefsDigest,
    string[] ActiveDlc, string ValuationVersion, long[] AffectedOwners, int AffectedPreparations, int AffectedOutposts);

public sealed partial class OutpostAdminService
{
    public Task<OutpostManifestPage> ReadManifestPageAsync(Actor actor, OutpostManifestPageRequest request, CancellationToken ct)
    {
        actor.Require(Role.Admin); var selected = request.Clone();
        return authority.ReadAdminProjectionAsync(observation, live, async (observed, token) =>
        {
            if (observed.Kind != OutpostAuthorityObservationKind.Committed) throw new OutpostEvidenceUnavailableException("admin-inspection-evidence-unavailable");
            var row = await db.OutpostManifests.AsNoTracking().SingleOrDefaultAsync(m => m.Id == selected.ManifestId, token);
            if (row is null || row.Document.Length is < 1 or > OutpostManifestService.MaxDocumentBytes
                || selected.ExpectedDigest != Convert.ToHexStringLower(row.Digest) || !SHA256.HashData(row.Document).AsSpan().SequenceEqual(row.Digest)
                || selected.Offset < 0 || selected.Offset >= row.Document.Length) throw new OutpostEvidenceUnavailableException("admin-manifest-evidence-invalid");
            return new OutpostManifestPage { Data = ByteString.CopyFrom(row.Document, selected.Offset, Math.Min(OutpostManifestService.MaxChunkBytes, row.Document.Length - selected.Offset)),
                TotalBytes = row.Document.Length, Digest = selected.ExpectedDigest };
        }, ct);
    }
    public Task<OutpostAdminManifestView> InspectManifestAsync(Actor actor, string id, CancellationToken ct)
    {
        actor.Require(Role.Moderator);
        if (!Guid.TryParseExact(id, "D", out var parsed) || parsed == Guid.Empty) throw new InvalidDataException("outpost-admin-query-invalid");
        return authority.ReadAdminProjectionAsync(observation, live, async (observed, token) =>
        {
            if (observed.Kind != OutpostAuthorityObservationKind.Committed) throw new OutpostEvidenceUnavailableException("admin-inspection-evidence-unavailable");
            var row = await db.OutpostManifests.AsNoTracking().SingleOrDefaultAsync(m => m.Id == id, token)
                ?? throw new OutpostEvidenceUnavailableException("admin-manifest-unavailable");
            if (row.Document.Length is < 1 or > OutpostManifestService.MaxDocumentBytes || !SHA256.HashData(row.Document).AsSpan().SequenceEqual(row.Digest))
                throw new OutpostEvidenceUnavailableException("admin-manifest-evidence-invalid");
            var document = OutpostManifestDocument.Parser.ParseFrom(row.Document);
            var owners = await db.Outposts.AsNoTracking().Where(o => o.OccupiesSlot).Select(o => o.PlayerId).Distinct().Order().ToArrayAsync(token);
            return new OutpostAdminManifestView(row.Id, row.Revision, row.Digest, row.Confirmed, row.Active, document.Binding,
                document.MainSurfaceId, document.TileCount, document.GameVersion, document.GameDefsSha256.ToByteArray(),
                document.ActiveDlc.ToArray(), document.ValuationVersion, owners,
                await db.OutpostOperations.CountAsync(o => o.State == "Prepared" || o.State == "CancelFenced", token),
                await db.Outposts.CountAsync(o => o.OccupiesSlot, token));
        }, ct);
    }
}
