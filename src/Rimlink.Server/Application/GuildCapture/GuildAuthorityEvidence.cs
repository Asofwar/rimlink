using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Storage;
using Rimlink.Server.Storage.Entities;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;

namespace Rimlink.Server.Application.GuildCapture;

internal static partial class GuildAuthorityEvidence
{
    private static readonly JsonSerializerOptions Json = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    internal static bool IsKind(string kind) => kind is GuildAcceptanceClockRules.Kind or AcceptanceBindingKind or "GuildPlacement" or "GuildConsent" or "GuildProduction" or "GuildGovernance" or "GuildResourceClaim" or "GuildBeginDelivery"
        or "GuildCampaignScheduled" or "GuildBattlePrepared" or "GuildBattleProfileApproved" or "GuildBattleProfileRevoked" or "GuildAttackerReady" or "GuildBattleStarted" or "GuildBattleApplied" or "GuildExecutorCleanup" or "GuildBattleControlReconciled" or "GuildDefenderCommanderAssigned" or "GuildCampaignExpired" or GuildOperatorCancellation.Kind or GuildFreezeReceipt.Kind or GuildStartedAbortReceipt.Kind;

    internal static ByteString Binding(GuildCaptureOperation operation) => ByteString.CopyFrom(JsonSerializer.SerializeToUtf8Bytes(operation, Json));

    internal static async Task ValidateStoredAsync(RimlinkDbContext db, IEnumerable<OutpostTransition> transitions,
        OutpostSchemaObservation schema, BattleProfileArchive archive, BattleArtifactStore artifacts, CancellationToken ct)
    {
        var byId = transitions.ToDictionary(t => t.Id, StringComparer.Ordinal);
        var all = byId.Values.Where(t => IsKind(t.Kind)).ToDictionary(t => t.Id, StringComparer.Ordinal);
        if (!OutpostDomainSchema.HasGuildTables(schema))
        {
            if (all.Count != 0) throw new OutpostEvidenceUnavailableException("guild-operation-schema-missing");
            return;
        }
        var operations = await db.Set<GuildCaptureOperation>().AsNoTracking().ToArrayAsync(ct);
        foreach (var operation in operations)
        {
            if (operation.Kind == GuildSnapshotGuard.MarkerKind)
            {
                await GuildSnapshotGuard.ValidateMarkerAsync(db, operation, byId, ct);
                continue;
            }
            if (!all.TryGetValue(operation.TransitionId, out var transition) || operation.ActorId != transition.PlayerId
                || operation.Kind != transition.Kind || operation.IntentDigest is not { Length: 32 }
                || operation.Receipt.Length is < 1 or > 8192 || !Guid.TryParseExact(operation.Id, "D", out _)
                || !Guid.TryParseExact(operation.RequestId, "D", out _) || !Binding(operation).Span.SequenceEqual(transition.Intent))
                throw new OutpostEvidenceUnavailableException("guild-operation-evidence-mismatch");
            if (operation.Kind == "GuildGovernance")
            {
                GuildGovernanceReceipt? receipt;
                try { receipt = JsonSerializer.Deserialize<GuildGovernanceReceipt>(operation.Receipt, Json); }
                catch (JsonException error) { throw new OutpostEvidenceUnavailableException("guild-governance-receipt-invalid: " + error.GetType().Name); }
                if (receipt is null || receipt.RequestId is null || receipt.Result is null
                    || receipt.RequestId.Length is < 1 or > 64 || GuildLifecycle.OperationRequestId(operation.ActorId, receipt.RequestId) != operation.RequestId)
                    throw new OutpostEvidenceUnavailableException("guild-governance-receipt-invalid");
                var source = await db.Set<CommunityOperation>().AsNoTracking().SingleOrDefaultAsync(o => o.PlayerId == operation.ActorId && o.RequestId == receipt.RequestId, ct);
                if (source is null || source.Intent.Length > 8192 || !SHA256.HashData(source.Intent).AsSpan().SequenceEqual(operation.IntentDigest)
                    || !source.Result.AsSpan().SequenceEqual(receipt.Result)) throw new OutpostEvidenceUnavailableException("guild-governance-source-mismatch");
            }
            all.Remove(transition.Id);
        }
        if (all.Count != 0) throw new OutpostEvidenceUnavailableException("guild-operation-evidence-missing");
        ValidateAcceptanceBindings(operations,byId);
        ValidateFreezes(operations,byId);
        ValidateClockSteps(operations,byId);
        await ValidateClaimsAsync(db, operations, ct);
        await ValidateDeliveryStartsAsync(db, operations, byId, ct);
        await ValidateCampaignSchedulesAsync(db, operations, byId, ct);
        await ValidatePreparationsAsync(db, operations, byId,archive, ct);
        ValidateProfileApprovals(archive, operations, byId);
        await ValidateReadyAsync(db, artifacts, operations, byId, ct);
        await ValidateStartsAsync(db, artifacts, operations, byId, ct);
        await ValidateApplicationsAsync(db, artifacts, operations, byId, ct);
        await ValidateStartedAbortsAsync(db, operations, byId, ct);
        await ValidateCleanupsAsync(db, operations, byId, archive, ct);
        await ValidateControlsAsync(db, operations, byId, ct);
        await ValidateExpiriesAsync(db,artifacts,operations,byId,ct);
    }

    private static async Task ValidateClaimsAsync(RimlinkDbContext db, GuildCaptureOperation[] operations, CancellationToken ct)
    {
        var claims = await db.Set<GuildResourceClaim>().AsNoTracking().ToDictionaryAsync(c => c.Id, ct);
        if (operations.Count(o => o.Kind == "GuildResourceClaim") != claims.Count) throw InvalidClaim();
        var links = await db.Set<GuildParcelLink>().AsNoTracking().ToArrayAsync(ct);
        foreach (var link in links)
        {
            if (!claims.Remove(link.ClaimId, out var claim) || link.Payload.Length is < 1 or > 4096 || link.PayloadDigest.Length != 32
                || !SHA256.HashData(link.Payload).AsSpan().SequenceEqual(link.PayloadDigest)) throw InvalidClaim();
            var operation = operations.SingleOrDefault(o => o.TransitionId == claim.TransitionId);
            if (operation is null || operation.Kind != "GuildResourceClaim" || operation.ActorId != claim.ActorId || operation.RequestId != claim.RequestId
                || !operation.IntentDigest.AsSpan().SequenceEqual(claim.IntentDigest)) throw InvalidClaim();
            GuildResourceParcelPayload payload;
            GuildResourceClaimResult? receipt;
            try
            {
                payload = GuildResourceParcelPayload.Parser.ParseFrom(link.Payload);
                if (GuildResourceContracts.Validate(payload) != link.ValueX100) throw InvalidClaim();
                receipt = JsonSerializer.Deserialize<GuildResourceClaimResult>(operation.Receipt, Json);
            }
            catch (Exception error) when (error is InvalidProtocolBufferException or InvalidDataException or JsonException)
            { throw InvalidClaim(); }
            if (receipt is null || receipt.ClaimId != claim.Id || receipt.ParcelId != link.ParcelId || receipt.TransitionId != claim.TransitionId
                || receipt.PayloadDigest != Convert.ToHexStringLower(link.PayloadDigest) || payload.ClaimId != claim.Id
                || payload.AllianceId != claim.AllianceId || payload.OriginId != claim.OriginId || payload.OriginKind != claim.OriginKind
                || payload.Recipient.WorldInstanceId != claim.WorldId || payload.Recipient.RestoreBoundary != claim.Boundary
                || payload.Recipient.PlayerId != claim.RecipientId || payload.Recipient.SaveGeneration != claim.SaveGeneration
                || payload.Recipient.SaveWriteEpoch != claim.SaveWriteEpoch || payload.Count != claim.Count || payload.TransitionId != claim.TransitionId
                || payload.ManifestId != claim.ManifestId || payload.ValuationRevision != claim.ValuationRevision
                || payload.DefName != (claim.Resource == GuildResource.Steel ? "Steel" : claim.Resource == GuildResource.Wood ? "WoodLog" : "")) throw InvalidClaim();
            var parcel = await db.Parcels.AsNoTracking().SingleOrDefaultAsync(p => p.Id == link.ParcelId, ct);
            if (parcel is null || parcel.ToPlayerId != claim.RecipientId || parcel.Kind != ParcelKindDb.GuildResources || parcel.Silver != 0
                || parcel.FromPlayerId is not null || parcel.Items.Length != 0 || parcel.Pawns.Length != 0
                || parcel.Decision is ParcelDecisionDb.Awaiting or ParcelDecisionDb.Refused
                || await db.OutpostParcelLinks.AnyAsync(p => p.ParcelId == parcel.Id, ct)) throw InvalidClaim();
            if (link.AcceptedSnapshotId is null ? claim.State != "Pending" || parcel.DeliveredAt is not null
                : claim.State != "Accepted" || parcel.DeliveredAt is null || parcel.DeliveryRecoveryVersion != SaveRecoveryData.CurrentVersion
                    || parcel.DeliveredGeneration != claim.SaveGeneration) throw InvalidClaim();
            var manifest = await db.OutpostManifests.AsNoTracking().SingleOrDefaultAsync(m => m.Id == claim.ManifestId, ct);
            if (manifest is null || !manifest.Confirmed || manifest.WorldId != claim.WorldId || manifest.Revision != claim.ValuationRevision
                || !manifest.Digest.AsSpan().SequenceEqual(payload.ManifestDigest.Span)) throw InvalidClaim();
            var values = OutpostManifestDocument.Parser.ParseFrom(manifest.Document).Resources.Where(r => r.DefName == payload.DefName).ToArray();
            if (values.Length != 1 || values[0].UnitValueX100 != payload.UnitValueX100) throw InvalidClaim();
        }
        if (claims.Count != 0) throw InvalidClaim();
        static OutpostEvidenceUnavailableException InvalidClaim() => new("guild-resource-claim-evidence-mismatch");
    }

    internal static byte[] IntentDigest<T>(T intent) => SHA256.HashData(intent is IMessage proto ? proto.ToByteArray() : JsonSerializer.SerializeToUtf8Bytes(intent, Json));
}
