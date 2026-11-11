using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Rimlink.Server.Application.Admin;
using Rimlink.Protocol.V1;
using Role = Rimlink.Server.Storage.Entities.Role;

namespace Rimlink.Server.Application.Outposts;

public sealed record OutpostAdminQuery(long? PlayerId = null, string? OperationId = null, int Limit = 50, string? Cursor = null);
public sealed record OutpostAdminStock(int Whole, string Numerator, string Denominator, DateTime Checkpoint, int Tile, int Profile, bool OccupiesSlot);
public sealed record OutpostAdminSummary(string Kind, string Id, long? PlayerId, string State,
    OutpostAdminEvidenceReference[] Evidence, string? RequestId = null, string? RequestKind = null,
    byte[]? OriginalIntentDigest = null, OutpostScopeMessage? Scope = null, string? TransitionId = null, long? Revision = null,
    OutpostAdminStock? Stock = null, string EvidenceState = "Verified");
public sealed record OutpostAdminView(OutpostAuthorityObservation Observation, byte[] ObservationDigest,
    OutpostStartupResult Startup, bool? HasHistory, bool AdvertiseCapability, string[] Blockers,
    OutpostAdminSummary[] Rows, string? NextCursor, bool HasMore, string[] AvailableActions, OutpostReadinessDecision Readiness,
    bool CanAdmin, OutpostAdminPreflight[] Preflights);
public sealed record OutpostAdminPreflight(string Action, bool? Allowed, string[] Reasons);

public sealed partial class OutpostAdminService
{
    private static readonly string[] SummaryKinds = ["Custody", "Manifest", "Operation", "Ordinary", "Outpost", "Parcel"];
    private sealed record ReadCursor(int Version, byte[] ObservationDigest, long? PlayerId, string? OperationId, int Limit, string Kind, string Id);

    public Task<OutpostAdminView> InspectAsync(Actor actor, OutpostAdminQuery query, CancellationToken ct)
    {
        actor.Require(Role.Moderator);
        if (query is null || query.PlayerId is <= 0 || query.Limit is < 1 or > 100
            || query.OperationId is { } id && (string.IsNullOrWhiteSpace(id) || id.Length > 256 || id.Any(char.IsControl))
            || query.Cursor?.Length > 8192) throw InvalidRead();
        var frozen = query with { }; // Strings are immutable; the DTO has no caller-owned buffers.
        ReadCursor? cursor = ReadContinuation(frozen);
        return authority.ReadAdminProjectionAsync(observation, live, async (observed, token) =>
        {
            var observationDigest = OutpostAdminIntentCodec.ObservationDigest(observed);
            if (cursor is not null && !cursor.ObservationDigest.AsSpan().SequenceEqual(observationDigest))
                throw new OutpostEvidenceUnavailableException("admin-inspection-cut-changed");
            var rows = new List<OutpostAdminSummary>();
            if (observed.Kind == OutpostAuthorityObservationKind.Committed)
                foreach (string kind in SummaryKinds)
                {
                    if (frozen.OperationId is not null && kind != "Operation" || cursor is not null && string.CompareOrdinal(kind, cursor.Kind) < 0) continue;
                    rows.AddRange(await ReadSummariesAsync(kind, frozen, cursor?.Kind == kind ? cursor.Id : null,
                        frozen.Limit + 1 - rows.Count, token));
                    if (rows.Count > frozen.Limit) break;
                }
            bool more = rows.Count > frozen.Limit;
            if (more) rows.RemoveAt(rows.Count - 1);
            string? next = more ? Convert.ToBase64String(OutpostRestoreEvidence.Encode(new ReadCursor(1, observationDigest,
                frozen.PlayerId, frozen.OperationId, frozen.Limit, rows[^1].Kind, rows[^1].Id))) : null;
            var readiness = authority.ReadinessObservation;
            string[] blockers = observed.Kind == OutpostAuthorityObservationKind.Unavailable
                ? ["admin-inspection-evidence-unavailable", .. readiness.Blockers]
                : readiness.Blockers;
            var result = new OutpostAdminView(observed, observationDigest, readiness.Startup,
                observed.Kind == OutpostAuthorityObservationKind.Unavailable ? null : observed.Kind == OutpostAuthorityObservationKind.Committed,
                readiness.AdvertiseCapability && observed.Kind == OutpostAuthorityObservationKind.Committed, blockers, rows.ToArray(), next, more,
                observed.Kind == OutpostAuthorityObservationKind.Uninitialized ? ["PreviewInitialize"]
                    : observed.Committed?.DomainFormat == OutpostDomainFormat.V1 ? ["PreviewDomainFormatActivation"]
                    : observed.Committed?.DomainFormat is OutpostDomainFormat.V2 or OutpostDomainFormat.V3 ? ["PreviewManifestConfirmation", "PreviewResolve"] : [], readiness,
                actor.Role >= Role.Admin,
                new[] { "archive", "reset", "backup" }.Select(action => new OutpostAdminPreflight(action, readiness.CanResolveCustody ? null : false,
                    readiness.CanResolveCustody ? ["explicit-locked-" + action + "-validation-required"] : readiness.Blockers)).ToArray());
            // Conservative service budget allows for hex rather than base64 in the remote representation.
            if (JsonSerializer.SerializeToUtf8Bytes(result).Length > 512 * 1024) throw InvalidRead();
            return result;
        }, ct);
    }

    private static ReadCursor? ReadContinuation(OutpostAdminQuery query)
    {
        if (query.Cursor is null) return null;
        try
        {
            byte[] bytes = Convert.FromBase64String(query.Cursor);
            if (bytes.Length is < 1 or > 4096) throw InvalidRead();
            var cursor = OutpostRestoreEvidence.Decode<ReadCursor>(bytes);
            if (cursor.Version != 1 || cursor.ObservationDigest.Length != 32 || cursor.PlayerId != query.PlayerId
                || cursor.OperationId != query.OperationId || cursor.Limit != query.Limit || !SummaryKinds.Contains(cursor.Kind)
                || cursor.Id.Length is < 1 or > 256 || cursor.Id.Any(char.IsControl))
                throw new OutpostEvidenceUnavailableException("admin-inspection-cut-changed");
            return cursor;
        }
        catch (Exception error) when (error is FormatException or JsonException) { throw InvalidRead(); }
    }

    private async Task<OutpostAdminSummary[]> ReadSummariesAsync(string kind, OutpostAdminQuery query, string? after, int take, CancellationToken ct)
    {
        switch (kind)
        {
            case "Operation":
                var operations = await db.OutpostOperations.AsNoTracking()
                    .Where(o => (!query.PlayerId.HasValue || o.PlayerId == query.PlayerId) && (query.OperationId == null || o.Id == query.OperationId)
                        && (after == null || o.Id.CompareTo(after) > 0))
                    .OrderBy(o => o.Id).Take(take).Select(o => new { o.Id, o.PlayerId, o.WorldId, o.SaveGeneration, o.SaveWriteEpoch,
                        o.RestoreBoundary, o.State, o.RequestId, o.Kind, o.IntentDigest, o.Intent, o.TransitionId }).ToArrayAsync(ct);
                return operations.Select(o => new OutpostAdminSummary(kind, o.Id, o.PlayerId, o.State,
                    [new(OutpostAdminEvidenceKind.Operation, o.Id, SHA256.HashData(o.Intent))], o.RequestId, o.Kind, o.IntentDigest,
                    new() { WorldInstanceId = o.WorldId, OwnerPlayerId = o.PlayerId, SaveGeneration = o.SaveGeneration,
                        SaveWriteEpoch = o.SaveWriteEpoch, RestoreBoundary = o.RestoreBoundary }, o.TransitionId)).ToArray();
            case "Custody":
                var pawns = await db.PawnCustody.AsNoTracking().Where(p => (!query.PlayerId.HasValue || p.PlayerId == query.PlayerId)
                    && (after == null || p.Id.CompareTo(after) > 0)).OrderBy(p => p.Id).Take(take).ToArrayAsync(ct);
                return pawns.Select(p => new OutpostAdminSummary(kind, p.Id, p.PlayerId, p.LocationKind,
                    [new(OutpostAdminEvidenceKind.Custody, p.Id, SHA256.HashData(OutpostRestoreEvidence.Encode(p)))],
                    TransitionId: p.FrontierTransitionId, Revision: p.Revision)).ToArray();
            case "Manifest":
                var catalogs = await db.OutpostManifests.AsNoTracking().Where(m => after == null || m.Id.CompareTo(after) > 0)
                    .OrderBy(m => m.Id).Take(take).Select(m => new { m.Id, m.Active, m.Confirmed, m.Digest, m.Revision }).ToArrayAsync(ct);
                return catalogs.Select(m => new OutpostAdminSummary(kind, m.Id, null, m.Active ? "Active" : m.Confirmed ? "Confirmed" : "Staged",
                    [new(OutpostAdminEvidenceKind.Manifest, m.Id, m.Digest)], Revision: m.Revision)).ToArray();
            case "Ordinary":
                var returns = await db.OutpostOrdinaryReturns.AsNoTracking().Where(r => (!query.PlayerId.HasValue || r.PlayerId == query.PlayerId)
                    && (after == null || r.Id.CompareTo(after) > 0)).OrderBy(r => r.Id).Take(take)
                    .Select(r => new { r.Id, r.PlayerId, r.State, r.PayloadDigest, r.RequestId, r.RequestKind }).ToArrayAsync(ct);
                return returns.Select(r => new OutpostAdminSummary(kind, r.Id, r.PlayerId, r.State,
                    [new(OutpostAdminEvidenceKind.OrdinarySource, r.Id, r.PayloadDigest)], r.RequestId, ((OrdinaryRequestKind)r.RequestKind).ToString())).ToArray();
            case "Outpost":
                var posts = await db.Outposts.AsNoTracking().Where(o => (!query.PlayerId.HasValue || o.PlayerId == query.PlayerId)
                    && (after == null || o.Id.CompareTo(after) > 0)).OrderBy(o => o.Id).Take(take).ToArrayAsync(ct);
                return posts.Select(o => new OutpostAdminSummary(kind, o.Id, o.PlayerId, o.State,
                    [new(OutpostAdminEvidenceKind.Manifest, o.ManifestId, o.ManifestDigest)], TransitionId: o.LastTransitionId, Revision: o.Revision,
                    Stock: new(o.Stock, o.FractionNumerator, o.FractionDenominator, o.CheckpointUtc, o.Tile, o.Profile, o.OccupiesSlot))).ToArray();
            case "Parcel":
                long? afterId = after is null ? null : long.TryParse(after, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value > 0
                    ? value : throw InvalidRead();
                var parcels = await db.OutpostParcelLinks.AsNoTracking().Where(p => (!query.PlayerId.HasValue || p.PlayerId == query.PlayerId)
                    && (!afterId.HasValue || p.ParcelId > afterId)).OrderBy(p => p.ParcelId).Take(take)
                    .Select(p => new { p.ParcelId, p.PlayerId, p.AcceptedSnapshotId, p.PayloadDigest, p.TransitionId }).ToArrayAsync(ct);
                return parcels.Select(p => new OutpostAdminSummary(kind, p.ParcelId.ToString(CultureInfo.InvariantCulture), p.PlayerId,
                    p.AcceptedSnapshotId is null ? "Pending" : "Accepted",
                    [new(OutpostAdminEvidenceKind.Parcel, p.ParcelId.ToString(CultureInfo.InvariantCulture), p.PayloadDigest)], TransitionId: p.TransitionId)).ToArray();
            default: throw InvalidRead();
        }
    }
    private static InvalidDataException InvalidRead() => new("outpost-admin-query-invalid");
}
