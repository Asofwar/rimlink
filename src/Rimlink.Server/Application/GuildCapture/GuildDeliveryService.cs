using System.Security.Cryptography;
using System.Text.Json;
using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Rimlink.Server.Storage.Entities;

namespace Rimlink.Server.Application.GuildCapture;

internal sealed record GuildDeliveryStart(string AttemptId, long ParcelId, byte[] Request);

internal sealed partial class GuildOutpostService
{
    internal async Task<GuildDeliveryAuthorization> BeginDeliveryAsync(long recipient, GuildDeliveryRequest request, CancellationToken ct)
    {
        if (request is null || request.CalculateSize() > 4096 || recipient <= 0 || request.Scope?.PlayerId != recipient
            || request.ParcelId <= 0 || request.PayloadDigest.Length != 32 || request.BeforeSnapshot is not { } before
            || !Guid.TryParseExact(before.SnapshotId, "N", out _) || before.RawSha256.Length != 32 || !request.Scope.Equals(before.Scope)) throw Refused();
        try { GuildResourceContracts.ValidateScope(request.Scope); }
        catch (InvalidDataException) { throw Refused(); }
        request = request.Clone();
        var result = await MutateAsync(new(request.Scope.WorldInstanceId, request.Scope.RestoreBoundary), recipient, "player:" + recipient,
            request.ClientRequestId, "GuildBeginDelivery", request, async (_, id, token) =>
            {
                var link = await db.Set<GuildParcelLink>().AsNoTracking().SingleOrDefaultAsync(p => p.ParcelId == request.ParcelId, token) ?? throw Refused();
                var claim = await db.Set<GuildResourceClaim>().AsNoTracking().SingleAsync(c => c.Id == link.ClaimId, token);
                if (!(await GuildRestore.DeliveryScopeAsync(db, claim, link, request.Scope.RestoreBoundary, token)).Equals(request.Scope)
                    || link.AcceptedSnapshotId is not null || !link.PayloadDigest.AsSpan().SequenceEqual(request.PayloadDigest.Span)
                    || await db.Set<GuildDeliveryAttempt>().AnyAsync(a => a.ParcelId == request.ParcelId && a.Active, token)) throw Refused();
                var parcel = await db.Parcels.AsNoTracking().SingleAsync(p => p.Id == request.ParcelId, token);
                if (parcel.ToPlayerId != recipient || parcel.DeliveredAt is not null || parcel.Decision is ParcelDecisionDb.Awaiting or ParcelDecisionDb.Refused) throw Refused();
                var source = await db.OutpostSnapshotLinks.AsNoTracking().SingleOrDefaultAsync(s => s.PlayerId == recipient && s.WorldId == claim.WorldId
                    && s.SaveGeneration == claim.SaveGeneration && s.SaveWriteEpoch == claim.SaveWriteEpoch && s.RestoreBoundary == request.Scope.RestoreBoundary
                    && s.SnapshotId == request.BeforeSnapshot.SnapshotId, token);
                var current = await db.SaveVersions.AsNoTracking().SingleOrDefaultAsync(s => s.PlayerId == recipient && s.IsCurrent, token);
                if (source is null || current is null || current.SnapshotId != source.SnapshotId || current.SaveGeneration != claim.SaveGeneration
                    || current.SaveWriteEpoch != claim.SaveWriteEpoch || !source.RawSha256.AsSpan().SequenceEqual(request.BeforeSnapshot.RawSha256.Span)
                    || current.RawSha256 is null || !current.RawSha256.AsSpan().SequenceEqual(source.RawSha256)
                    || OutpostSaveMetadata.Parser.ParseFrom(source.Metadata).GuildMetadata is null) throw Refused();
                // The common held authority has already checked these protected source bytes against their retained file.
                db.Add(new GuildDeliveryAttempt { Id = id, ParcelId = request.ParcelId, RequestId = request.ClientRequestId,
                    IntentDigest = GuildAuthorityEvidence.IntentDigest(request), RecipientId = recipient, WorldId = claim.WorldId, Boundary = request.Scope.RestoreBoundary,
                    SaveGeneration = claim.SaveGeneration, SaveWriteEpoch = claim.SaveWriteEpoch, BeforeSnapshotId = source.SnapshotId,
                    BeforeSnapshotDigest = source.RawSha256.ToArray(), Active = true });
                return new GuildDeliveryStart(id, request.ParcelId, request.ToByteArray());
            }, ct, validateScope: async token =>
            {
                var player = await db.Players.AsNoTracking().SingleOrDefaultAsync(p => p.Id == recipient, token);
                if (player is null || player.SaveGeneration != request.Scope.SaveGeneration || player.SaveWriteEpoch != request.Scope.SaveWriteEpoch
                    || !await db.WorldObjects.AnyAsync(o => o.OwnerPlayerId == recipient && o.Type == WorldObjectTypes.Settlement, token)) throw Refused();
            }, saveGeneration: request.Scope.SaveGeneration);
        var attempt = await db.Set<GuildDeliveryAttempt>().AsNoTracking().SingleAsync(a => a.Id == result.AttemptId, ct);
        var transition = await db.OutpostTransitions.AsNoTracking().SingleAsync(t => t.Id == attempt.Id, ct);
        var reply = new GuildDeliveryAuthorization { AttemptId = attempt.Id, Request = GuildDeliveryRequest.Parser.ParseFrom(result.Request),
            TransitionId = transition.Id, TransitionDigest = ByteString.CopyFrom(transition.Digest), IntentDigest = ByteString.CopyFrom(attempt.IntentDigest),
            AckOnly = attempt.AcceptedSnapshotId is not null };
        if (attempt.AcceptedSnapshotId is not null)
        {
            var accepted = await db.OutpostSnapshotLinks.AsNoTracking().SingleAsync(s => s.PlayerId == recipient && s.SnapshotId == attempt.AcceptedSnapshotId, ct);
            reply.AcceptedSnapshot = new() { SnapshotId = accepted.SnapshotId, RawSha256 = ByteString.CopyFrom(accepted.RawSha256),
                Scope = new() { WorldInstanceId = accepted.WorldId, PlayerId = recipient, SaveGeneration = accepted.SaveGeneration,
                    SaveWriteEpoch = accepted.SaveWriteEpoch, RestoreBoundary = accepted.RestoreBoundary } };
        }
        return reply;
    }
}

internal static partial class GuildAuthorityEvidence
{
    private static async Task ValidateDeliveryStartsAsync(Storage.RimlinkDbContext db, GuildCaptureOperation[] operations,
        IReadOnlyDictionary<string, OutpostTransition> transitions, CancellationToken ct)
    {
        var starts = operations.Where(o => o.Kind == "GuildBeginDelivery").ToDictionary(o => o.TransitionId, StringComparer.Ordinal);
        var attempts = await db.Set<GuildDeliveryAttempt>().AsNoTracking().ToArrayAsync(ct);
        if (starts.Count != attempts.Length) throw Invalid();
        foreach (var attempt in attempts)
        {
            if (!starts.TryGetValue(attempt.Id, out var operation) || operation.ActorId != attempt.RecipientId
                || operation.RequestId != attempt.RequestId || !operation.IntentDigest.AsSpan().SequenceEqual(attempt.IntentDigest)
                || !transitions.TryGetValue(attempt.Id, out var transition) || transition.Kind != "GuildBeginDelivery"
                || transition.WorldId != attempt.WorldId || transition.RestoreBoundary != attempt.Boundary || transition.SaveGeneration != attempt.SaveGeneration) throw Invalid();
            GuildDeliveryStart? stored;
            GuildDeliveryRequest request;
            try
            {
                stored = JsonSerializer.Deserialize<GuildDeliveryStart>(operation.Receipt, Json);
                if (stored?.Request is not { Length: > 0 and <= 4096 }) throw Invalid();
                request = GuildDeliveryRequest.Parser.ParseFrom(stored.Request);
                GuildResourceContracts.ValidateScope(request.Scope);
            }
            catch (Exception error) when (error is JsonException or InvalidProtocolBufferException or InvalidDataException) { throw Invalid(); }
            var before = request.BeforeSnapshot;
            if (stored.AttemptId != attempt.Id || stored.ParcelId != attempt.ParcelId || request.ParcelId != attempt.ParcelId
                || request.ClientRequestId != attempt.RequestId || !IntentDigest(request).AsSpan().SequenceEqual(attempt.IntentDigest)
                || request.Scope.PlayerId != attempt.RecipientId || request.Scope.WorldInstanceId != attempt.WorldId
                || request.Scope.SaveGeneration != attempt.SaveGeneration || request.Scope.SaveWriteEpoch != attempt.SaveWriteEpoch
                || request.Scope.RestoreBoundary != attempt.Boundary || before is null || !before.Scope.Equals(request.Scope)
                || before.SnapshotId != attempt.BeforeSnapshotId || !before.RawSha256.Span.SequenceEqual(attempt.BeforeSnapshotDigest)) throw Invalid();
            var link = await db.Set<GuildParcelLink>().AsNoTracking().SingleOrDefaultAsync(p => p.ParcelId == attempt.ParcelId, ct);
            bool retired = !attempt.Active && attempt.AcceptedSnapshotId is null;
            if (link is null || !request.PayloadDigest.Span.SequenceEqual(link.PayloadDigest)
                || !retired && (link.AcceptedSnapshotId != attempt.AcceptedSnapshotId || attempt.Active != (attempt.AcceptedSnapshotId is null))
                || attempt.Active && attempt.Receipt is not null) throw Invalid();
            if (retired)
            {
                var proof = transitions.Values.Where(t => t.Kind == "BulkRestore").Select(t => (Root: t, Plan: Outposts.OutpostRestoreEvidence.Plan(t)))
                    .SingleOrDefault(p => p.Plan.Guild?.RetiredAttempts?.Any(m => Outposts.OutpostRestoreEvidence.Same(m.After, attempt)) == true);
                if (proof.Root is null || !proof.Plan.Players.Any(p => p.Canonical is { } canonical && GuildRestore.ExactBefore(canonical, attempt)))
                    throw Invalid();
                RequireEarlier(transitions, attempt.Id, proof.Root.Id);
            }
            var claim = await db.Set<GuildResourceClaim>().AsNoTracking().SingleAsync(c => c.Id == link.ClaimId, ct);
            if (!(await GuildRestore.DeliveryScopeAsync(db, claim, link, attempt.Boundary, ct)).Equals(request.Scope)) throw Invalid();
            var source = await db.OutpostSnapshotLinks.AsNoTracking().SingleOrDefaultAsync(s => s.PlayerId == attempt.RecipientId && s.WorldId == attempt.WorldId
                && s.SaveGeneration == attempt.SaveGeneration && s.SaveWriteEpoch == attempt.SaveWriteEpoch && s.RestoreBoundary == attempt.Boundary
                && s.SnapshotId == attempt.BeforeSnapshotId, ct);
            if (source is null || !source.RawSha256.AsSpan().SequenceEqual(attempt.BeforeSnapshotDigest)
                || OutpostSaveMetadata.Parser.ParseFrom(source.Metadata).GuildMetadata is null) throw Invalid();
            if (attempt.AcceptedSnapshotId is not null)
            {
                if (attempt.Receipt is not { Length: > 0 and <= 4096 }) throw Invalid();
                GuildDeliveryReceipt receipt;
                try { receipt = GuildDeliveryReceipt.Parser.ParseFrom(attempt.Receipt); }
                catch (InvalidProtocolBufferException) { throw Invalid(); }
                var accepted = await db.OutpostSnapshotLinks.AsNoTracking().SingleOrDefaultAsync(s => s.PlayerId == attempt.RecipientId && s.SnapshotId == attempt.AcceptedSnapshotId, ct);
                if (accepted is null) throw Invalid();
                await GuildSnapshotGuard.ValidateReceiptAsync(db, OutpostSaveMetadata.Parser.ParseFrom(accepted.Metadata).Scope, receipt, ct);
            }
        }
        static Outposts.OutpostEvidenceUnavailableException Invalid() => new("guild-delivery-start-evidence-mismatch");
    }
}
