using System.Security.Cryptography;
using System.Text.Json;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Saves;
using Rimlink.Server.Grpc;
using Rimlink.Server.Storage.Entities;
using Role = Rimlink.Server.Storage.Entities.Role;
using Rimlink.Server.Application.Admin;
using Rimlink.Server.Storage;

namespace Rimlink.Server.Application.Outposts;

public sealed partial class OutpostManifestService(RimlinkDbContext db, OutpostAuthority authority, OutpostClock clock, SaveFiles files)
{
    public const string MainSurface = "planet";
    public const int MaxDocumentBytes = 24 * 1024 * 1024;
    public const int MaxChunkBytes = 256 * 1024;
    public async Task<OutpostManifestBinding> BindingAsync(Actor actor, CancellationToken ct)
    {
        actor.Require(Role.Admin);
        using var held = await files.PerStoreLock.HoldAsync(ct);
        return await BindingHeldAsync(ct);
    }

    private async Task<OutpostManifestBinding> BindingHeldAsync(CancellationToken ct)
    {
        var world = await db.World.AsNoTracking().SingleOrDefaultAsync(ct) ?? throw Invalid("world-missing");
        var entries = await db.ModpackEntries.AsNoTracking().OrderBy(e => e.Position).ThenBy(e => e.PackageId)
            .Select(e => new { e.Position, e.PackageId, e.Version, e.WorkshopId, e.Url, e.Kind, e.FilesHash }).ToArrayAsync(ct);
        var configs = await db.ModpackConfigs.AsNoTracking().OrderBy(c => c.PackageId).ThenBy(c => c.Handle).ToArrayAsync(ct);
        var defs = await db.ModpackDefs.AsNoTracking().OrderBy(d => d.DefName).Select(d => d.DefName).ToArrayAsync(ct);
        byte[] catalog = JsonSerializer.SerializeToUtf8Bytes(new { Format = "outpost-catalog-v1", Entries = entries,
            Configs = configs.Select(c => new { c.PackageId, c.Handle, Hash = Convert.ToHexStringLower(SHA256.HashData(c.Content)) }), Defs = defs });
        return new() { WorldInstanceId = world.InfluenceInstanceId, WorldParamsSha256 = ByteString.CopyFrom(SHA256.HashData(world.Params)),
            ServerCatalogSha256 = ByteString.CopyFrom(SHA256.HashData(catalog)) };
    }

    private static RpcException Invalid(string reason) => RpcErrors.With(StatusCode.FailedPrecondition, ErrorCode.OutpostManifestMismatch, RefusalKeys.OutpostsManifest, reason);

    public async Task<ManifestPreview> StageAsync(Actor actor, OutpostManifestDocument document, CancellationToken ct)
    {
        actor.Require(Role.Admin);
        if (document is null || document.CalculateSize() > MaxDocumentBytes) throw Invalid("manifest-too-large");
        document = document.Clone();
        await using var lease = await authority.AcquireAsync([], ct);
        await ValidateHeldAsync(document, ct);
        byte[] bytes = document.ToByteArray(), digest = SHA256.HashData(bytes);
        var existing = await db.OutpostManifests.AsNoTracking().FirstOrDefaultAsync(m => m.WorldId == document.Binding.WorldInstanceId &&
            (!m.Confirmed || m.Active) && m.Digest == digest, ct);
        if (existing is not null) return Preview(existing, document);
        var row = new OutpostManifest { Id = Guid.NewGuid().ToString("D"), WorldId = document.Binding.WorldInstanceId,
            Revision = (await db.OutpostManifests.Where(m => m.WorldId == document.Binding.WorldInstanceId).MaxAsync(m => (long?)m.Revision, ct) ?? 0) + 1,
            Digest = digest, Document = bytes, Actor = actor.Name, CreatedAt = clock.Sample().Utc };
        var intent = await IntentAsync("StageManifest", actor, row, ct);
        await authority.CommitHeldAsync(lease, intent, (context, token) => { context.OutpostManifests.Add(row); return Task.CompletedTask; }, ct);
        return Preview(row, document);
    }
    internal async Task<ManifestRevision> ConfirmAsync(Actor actor, string manifestId, string expectedDigest, CancellationToken ct)
    {
        actor.Require(Role.Admin);
        if (authority.IsRuntimeComposition) throw RpcErrors.Failed(ErrorCode.RecoveryRequired, RefusalKeys.OutpostsOperation, "canonical manifest command required");
        // Owner discovery is repeated after A/O/S acquisition: a racing admission must not escape the owner fence.
        while (true)
        {
            var owners = await db.Outposts.AsNoTracking().Where(o => o.OccupiesSlot).Select(o => o.PlayerId).Distinct().ToArrayAsync(ct);
            await using var lease = await authority.AcquireAsync(owners, ct);
            var affected = await db.Outposts.AsNoTracking().Where(o => o.OccupiesSlot).ToListAsync(ct);
            if (affected.Any(o => !lease.CoversOwner(o.PlayerId))) continue;
            var row = await FindHeldAsync(manifestId, expectedDigest, ct);
            var document = OutpostManifestDocument.Parser.ParseFrom(row.Document);
            await ValidateHeldAsync(document, ct);
            if (row.Active && row.Confirmed) return Revision(row);
            if (await db.OutpostManifests.AnyAsync(m => m.WorldId == row.WorldId && m.Active && m.Revision >= row.Revision, ct))
                throw Invalid("manifest-revision-obsolete");
            var provenanceIds = affected.Select(o => o.ManifestId).Distinct().ToArray();
            var provenance = await db.OutpostManifests.AsNoTracking().Where(m => provenanceIds.Contains(m.Id)).ToDictionaryAsync(m => m.Id, ct);
            var intent = await IntentAsync("ConfirmManifest", actor, row, ct);
            await authority.CommitHeldAsync(lease, intent, async (context, token) =>
            {
                // Same held staging rules are reused by the canonical admin command path.
                await StageConfirmationCoreAsync(row, document, affected, provenance, intent.Id, clock.Sample().Utc, token);
            }, ct);
            return Revision(row);
        }
    }
    public async Task<OutpostManifestDocument> RequireActiveHeldAsync(OutpostLease lease, string surfaceId, int tile, OutpostProfile profile, CancellationToken ct)
    {
        lease.Require(authority);
        string worldId = await db.OutpostAuthority.AsNoTracking().Select(a => a.WorldId).SingleAsync(ct);
        var row = await db.OutpostManifests.AsNoTracking().SingleOrDefaultAsync(m => m.Active && m.Confirmed && m.WorldId == worldId, ct)
            ?? throw Invalid("manifest-not-confirmed");
        CheckBytes(row);
        var document = OutpostManifestDocument.Parser.ParseFrom(row.Document);
        await ValidateHeldAsync(document, ct);
        if (surfaceId != MainSurface || !Eligible(document, tile, profile)) throw Invalid("manifest-tile-ineligible");
        return document;
    }
    public async Task<OutpostManifestPage> ReadAsync(Actor actor, OutpostManifestPageRequest request, CancellationToken ct)
    {
        actor.Require(Role.Admin);
        using var held = await files.PerStoreLock.HoldAsync(ct);
        var row = await FindHeldAsync(request.ManifestId, request.ExpectedDigest, ct);
        if (request.Offset < 0 || request.Offset >= row.Document.Length) throw Invalid("manifest-page-range");
        return new() { Data = ByteString.CopyFrom(row.Document, request.Offset, Math.Min(MaxChunkBytes, row.Document.Length - request.Offset)),
            TotalBytes = row.Document.Length, Digest = Convert.ToHexStringLower(row.Digest) };
    }

    public async Task<OutpostManifestPage> ReadConfirmedHeldAsync(OutpostLease lease, OutpostManifestReadRequest request, CancellationToken ct)
    {
        lease.Require(authority);
        var row = await FindHeldAsync(request.ManifestId, request.ExpectedDigest, ct);
        if (!row.Confirmed || row.WorldId != request.Scope.WorldInstanceId) throw Invalid("manifest-not-confirmed");
        if (request.ParcelId > 0)
        {
            var parcel = await db.OutpostParcelLinks.AsNoTracking().SingleOrDefaultAsync(p => p.ParcelId == request.ParcelId
                && p.WorldId == request.Scope.WorldInstanceId && p.PlayerId == request.Scope.OwnerPlayerId
                && p.SaveGeneration == request.Scope.SaveGeneration, ct);
            if (parcel is null || parcel.Payload.Length > OutpostRecoveryData.MaxMetadataBytes
                || !SHA256.HashData(parcel.Payload).AsSpan().SequenceEqual(parcel.PayloadDigest)) throw Invalid("manifest-parcel-mismatch");
            var payload = OutpostParcelPayload.Parser.ParseFrom(parcel.Payload);
            if (payload.ManifestId != row.Id || !payload.ManifestDigest.Span.SequenceEqual(row.Digest)) throw Invalid("manifest-parcel-mismatch");
            if (parcel.RestoreBoundary != request.Scope.RestoreBoundary
                && await OutpostRestoreEvidence.ReadParcelAuthorizationAsync(db, files, parcel, request.Scope, ct) is null)
                throw Invalid("manifest-parcel-mismatch");
            // The immutable parcel pins its original confirmed document and valuation.
        }
        else
        {
            if (!row.Active) throw Invalid("manifest-not-current-confirmed");
            await ValidateHeldAsync(OutpostManifestDocument.Parser.ParseFrom(row.Document), ct);
        }
        if (request.Offset < 0 || request.Offset >= row.Document.Length) throw Invalid("manifest-page-range");
        return new() { Data = ByteString.CopyFrom(row.Document, request.Offset, Math.Min(MaxChunkBytes, row.Document.Length - request.Offset)),
            TotalBytes = row.Document.Length, Digest = Convert.ToHexStringLower(row.Digest) };
    }

    private async Task ValidateHeldAsync(OutpostManifestDocument doc, CancellationToken ct)
    {
        if (doc.SchemaVersion != 1 || doc.Binding is null || !doc.Binding.Equals(await BindingHeldAsync(ct)) ||
            doc.MainSurfaceId != MainSurface || doc.TileCount is < 1 or > 2_000_000 || doc.Tiles.Count != doc.TileCount ||
            doc.GameDefsSha256.Length != 32 || doc.GameVersion.Length > 64 || !(doc.GameVersion == "1.7" || doc.GameVersion.StartsWith("1.7.", StringComparison.Ordinal)) ||
            string.IsNullOrWhiteSpace(doc.ValuationVersion) || doc.ValuationVersion.Length > 128 || doc.ActiveDlc.Count > 32 ||
            doc.ActiveDlc.Any(d => string.IsNullOrWhiteSpace(d) || d.Length > 128) ||
            !doc.ActiveDlc.SequenceEqual(doc.ActiveDlc.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)))
            throw Invalid("manifest-binding-or-shape");
        var known = (await db.ModpackDefs.AsNoTracking().Select(d => d.DefName).ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);
        for (int i = 0; i < doc.Tiles.Count; i++)
        {
            var tile = doc.Tiles[i];
            if (tile.Tile != i || tile.SurfaceId != MainSurface || tile.BiomeDef.Length > 200 || !known.Contains(tile.BiomeDef))
                throw Invalid("manifest-tile-domain");
        }
        if (doc.Resources.Count != 2 || doc.Resources[0].DefName != "Steel" || doc.Resources[1].DefName != "WoodLog" ||
            doc.Resources.Any(r => !known.Contains(r.DefName) || r.UnitValueX100 < 0 || r.UnitValueX100 > long.MaxValue / 1000))
            throw Invalid("manifest-valuations");
    }

    private async Task<TransitionIntent> IntentAsync(string kind, Actor actor, OutpostManifest row, CancellationToken ct)
    {
        var head = await db.OutpostAuthority.AsNoTracking().SingleAsync(ct);
        return new(Guid.NewGuid().ToString("D"), row.WorldId, 0, 0, head.RestoreBoundary, kind, actor.Name,
            new(head.HeadId, ByteString.CopyFrom(head.HeadDigest)), ByteString.CopyFromUtf8(row.Id + ":" + Convert.ToHexStringLower(row.Digest)));
    }

    private async Task<OutpostManifest> FindHeldAsync(string id, string digest, CancellationToken ct)
    {
        if (!Guid.TryParseExact(id, "D", out _) || digest.Length != 64) throw Invalid("manifest-identity");
        var row = await db.OutpostManifests.AsNoTracking().SingleOrDefaultAsync(m => m.Id == id, ct) ?? throw Invalid("manifest-missing");
        CheckBytes(row);
        if (Convert.ToHexStringLower(row.Digest) != digest) throw Invalid("manifest-digest-mismatch");
        return row;
    }

    private static void CheckBytes(OutpostManifest row)
    {
        if (row.Document.Length is < 1 or > MaxDocumentBytes || !SHA256.HashData(row.Document).AsSpan().SequenceEqual(row.Digest))
            throw Invalid("manifest-storage-corrupt");
    }

    private static bool Eligible(OutpostManifestDocument document, int tile, OutpostProfile profile)
    {
        if (tile < 0 || tile >= document.TileCount || profile is not (OutpostProfile.Wood or OutpostProfile.Steel)) return false;
        var t = document.Tiles[tile];
        return t.Land && t.CaravanPassable && (profile == OutpostProfile.Steel || t.NaturalTrees);
    }

    private static bool Compatible(OutpostManifest source, OutpostManifestDocument next, OutpostRow outpost)
    {
        CheckBytes(source);
        var previous = OutpostManifestDocument.Parser.ParseFrom(source.Document);
        return previous.Binding.Equals(next.Binding) && previous.GameVersion == next.GameVersion && previous.GameDefsSha256 == next.GameDefsSha256 &&
            previous.ActiveDlc.SequenceEqual(next.ActiveDlc) && Eligible(next, outpost.Tile, (OutpostProfile)outpost.Profile);
    }

    private static ManifestRevision Revision(OutpostManifest row) => new() { ManifestId = row.Id, Digest = Convert.ToHexStringLower(row.Digest), Revision = row.Revision };
    private static ManifestPreview Preview(OutpostManifest row, OutpostManifestDocument doc)
    {
        var preview = new ManifestPreview { ManifestId = row.Id, Digest = Convert.ToHexStringLower(row.Digest), Revision = row.Revision,
            Complete = true, ReceivedBytes = row.Document.Length, Binding = doc.Binding.Clone(), MainSurfaceId = doc.MainSurfaceId,
            TileCount = doc.TileCount, GameDefsSha256 = doc.GameDefsSha256, ValuationVersion = doc.ValuationVersion };
        preview.ActiveDlc.AddRange(doc.ActiveDlc); preview.Resources.AddRange(doc.Resources.Select(r => r.Clone()));
        return preview;
    }
}
