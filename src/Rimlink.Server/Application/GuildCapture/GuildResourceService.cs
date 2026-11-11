using System.Security.Cryptography;
using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Grpc;
using Rimlink.Server.Storage.Entities;

namespace Rimlink.Server.Application.GuildCapture;

internal sealed record GuildResourceClaimRequest(GuildMutationScope Scope, string RequestId, long AllianceId,
    string OriginKind, string OriginId, long OwnerRevision, long RecipientId, long RecipientGeneration, long RecipientEpoch, int Count);
internal sealed record GuildResourceClaimResult(string ClaimId, long ParcelId, string PayloadDigest, string TransitionId);

internal sealed partial class GuildOutpostService
{
    internal async Task<GuildResourceClaimResult> ClaimResourcesAsync(long leader, GuildResourceClaimRequest request, CancellationToken ct)
    {
        if (leader <= 0 || request is null || request.Scope is null || request.AllianceId <= 0 || request.Count is < 1 or > 1000
            || request.OwnerRevision <= 0 || request.RecipientId <= 0 || request.RecipientGeneration < 0 || request.RecipientEpoch < 0
            || request.OriginKind is not ("Outpost" or "Obligation") || !Guid.TryParseExact(request.OriginId, "D", out _)) throw GuildCapturePolicy.ClaimConflict();
        Storage.Entities.Parcel? parcelToAnnounce = null;
        var result = await MutateAsync(request.Scope, leader, "player:" + leader, request.RequestId, "GuildResourceClaim", new { leader, request }, async (lease, id, token) =>
        {
            await policy.RequireClaimPartiesHeldAsync(lease, leader, request, token);
            if (await GuildRestore.PendingDeliverySlotsFullAsync(db, token))
                throw RpcErrors.Failed(ErrorCode.OutpostConflict, RefusalKeys.GuildDeliveryCapacity,
                    "The world has reached the limit for pending guild resource deliveries.");
            GuildResource resource;
            var sample = clock.Sample();
            if (request.OriginKind == "Outpost")
            {
                var row = await db.Set<GuildOutpostRow>().SingleOrDefaultAsync(o => o.Id == request.OriginId && o.WorldId == request.Scope.WorldId, token);
                if (row is null || row.AllianceId != request.AllianceId || row.Revision != request.OwnerRevision || row.Phase != "Active")
                    throw GuildCapturePolicy.ClaimConflict();
                var checkpoint = new ProductionCheckpoint(row.CheckpointUtc, row.ClockProcessId, row.ClockTimestamp, row.ClockFrequency);
                var stock = GuildProduction.Settle(row.Resource, new(row.Stock, row.Remainder), OutpostClock.Elapsed(checkpoint, sample));
                if (stock.Count < request.Count) throw GuildCapturePolicy.ClaimConflict();
                var next = OutpostClock.Advance(checkpoint, sample);
                row.Stock = stock.Count - request.Count; row.Remainder = stock.Remainder;
                Checkpoint(row, new(next.Utc, next.ProcessId, next.Timestamp, next.Frequency)); resource = row.Resource;
            }
            else
            {
                var obligation = await db.Set<GuildResourceObligation>().SingleOrDefaultAsync(o => o.Id == request.OriginId, token);
                if (obligation is null || obligation.AllianceId != request.AllianceId || obligation.OwnerRevision != request.OwnerRevision
                    || obligation.RemainingCount - obligation.ReservedCount < request.Count) throw GuildCapturePolicy.ClaimConflict();
                if (!await db.Set<GuildOutpostRow>().AnyAsync(o => o.Id == obligation.OutpostId && o.WorldId == request.Scope.WorldId, token)) throw Refused();
                obligation.RemainingCount -= request.Count; resource = obligation.Resource;
            }
            var manifest = await db.OutpostManifests.AsNoTracking().SingleOrDefaultAsync(m => m.Active && m.Confirmed && m.WorldId == request.Scope.WorldId, token) ?? throw Refused();
            if (manifest.Document.Length is < 1 or > OutpostManifestService.MaxDocumentBytes
                || !SHA256.HashData(manifest.Document).AsSpan().SequenceEqual(manifest.Digest)) throw Refused();
            var document = OutpostManifestDocument.Parser.ParseFrom(manifest.Document);
            string def = resource == GuildResource.Steel ? "Steel" : resource == GuildResource.Wood ? "WoodLog" : throw Refused();
            var values = document.Resources.Where(r => r.DefName == def).ToArray();
            if (values.Length != 1) throw Refused();
            var payload = new GuildResourceParcelPayload { SchemaVersion = GuildResourceContracts.CurrentVersion,
                ClaimId = Guid.NewGuid().ToString("D"), AllianceId = request.AllianceId, OriginKind = request.OriginKind,
                OriginId = request.OriginId, OwnerRevision = request.OwnerRevision, Recipient = new() { WorldInstanceId = request.Scope.WorldId,
                    PlayerId = request.RecipientId, SaveGeneration = request.RecipientGeneration, SaveWriteEpoch = request.RecipientEpoch, RestoreBoundary = request.Scope.Boundary },
                DefName = def, Count = request.Count, UnitValueX100 = values[0].UnitValueX100, ManifestId = manifest.Id,
                ManifestDigest = ByteString.CopyFrom(manifest.Digest), ValuationRevision = manifest.Revision, TransitionId = id };
            long value = GuildResourceContracts.Validate(payload);
            byte[] bytes = payload.ToByteArray(), digest = SHA256.HashData(bytes);
            var parcel = await mailbox.Create(request.RecipientId, null, ParcelKindDb.GuildResources, null, null, 0, payload.ClaimId);
            parcel.SizeBytes = bytes.Length; parcel.CreatedAt = sample.Utc; parcel.RequestKind = "guild-resource-claim";
            db.Add(new GuildResourceClaim { Id = payload.ClaimId, RequestId = request.RequestId, ActorId = leader, AllianceId = request.AllianceId,
                IntentDigest = GuildAuthorityEvidence.IntentDigest(new { leader, request }), OriginKind = request.OriginKind, OriginId = request.OriginId,
                WorldId = request.Scope.WorldId, Boundary = request.Scope.Boundary, RecipientId = request.RecipientId, SaveGeneration = request.RecipientGeneration,
                SaveWriteEpoch = request.RecipientEpoch, Resource = resource, Count = request.Count, ManifestId = manifest.Id,
                ValuationRevision = manifest.Revision, TransitionId = id, CreatedAtUtc = sample.Utc });
            await db.SaveChangesAsync(token);
            db.Add(new GuildParcelLink { ParcelId = parcel.Id, ClaimId = payload.ClaimId, Payload = bytes, PayloadDigest = digest, ValueX100 = value });
            parcelToAnnounce = parcel;
            return new GuildResourceClaimResult(payload.ClaimId, parcel.Id, Convert.ToHexStringLower(digest), id);
        }, ct, [request.RecipientId]);
        if (parcelToAnnounce is not null) await mailbox.Announce(parcelToAnnounce);
        return result;
    }
}
