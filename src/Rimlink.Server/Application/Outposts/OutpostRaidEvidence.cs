using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Rimlink.Protocol.V1;
using Rimlink.Server.Storage;
using Rimlink.Server.Storage.Entities;

namespace Rimlink.Server.Application.Outposts;

public sealed partial class PawnCustodyBridge
{
    private static async Task<bool> RaidSourceMatchesAsync(RimlinkDbContext db, CustodyTransferEvidence evidence, CancellationToken ct)
    {
        if (evidence.Kind is not (CustodyTransferKind.CustodyTransferRaidLaunch or CustodyTransferKind.CustodyTransferRaidReturn or CustodyTransferKind.CustodyTransferRaidReport)) return false;
        var raid = await db.Raids.AsNoTracking().SingleOrDefaultAsync(r => r.Id == evidence.SourceId, ct);
        if (raid is null) return false;
        if (evidence.SourcePayload.Length > 0) return await RaidReportSourceMatchesAsync(db, raid, evidence, ct);
        if (raid.AttackerPlayerId != evidence.ActorPlayerId || !raid.Pawns.AsSpan().SequenceEqual(evidence.Pawns.ToByteArray())) return false;
        if (evidence.Kind == CustodyTransferKind.CustodyTransferRaidLaunch)
            return evidence.ParcelIds.Count == 0 && evidence.RecipientPlayerId == raid.TargetPlayerId
                && evidence.NonAuthoritativeMirror == (raid.PawnMode == (int)PawnMode.PawnSnapshot) && raid.ClientRequestId == evidence.ClientRequestId
                && evidence.SourceRequestIntent.Length == 32
                && await db.Transfers.AsNoTracking().AnyAsync(t => t.Kind == "raid" && t.FromPlayerId == raid.AttackerPlayerId
                    && t.ToPlayerId == raid.TargetPlayerId && t.RequestTargetId == raid.Id && t.ClientRequestId == evidence.ClientRequestId
                    && t.RequestIntent != null && t.RequestIntent.SequenceEqual(evidence.SourceRequestIntent.ToByteArray()), ct);
        if (evidence.Kind != CustodyTransferKind.CustodyTransferRaidReturn || raid.State is not (RaidStateDb.Cancelled or RaidStateDb.Expired) || raid.PawnMode != (int)PawnMode.PawnReal
            || evidence.NonAuthoritativeMirror || evidence.RecipientPlayerId != raid.AttackerPlayerId || evidence.ParcelIds.Count != 1) return false;
        var back = await db.Parcels.AsNoTracking().SingleOrDefaultAsync(p => p.Id == evidence.ParcelIds[0], ct);
        return back is not null && back.Kind == ParcelKindDb.RaidReturn && back.ToPlayerId == raid.AttackerPlayerId && back.FromPlayerId is null
            && back.Pawns.AsSpan().SequenceEqual(raid.Pawns);
    }

    public static PawnList RaidOutcomePawns(Storage.Entities.Raid raid, RaidReport report, params PawnOutcome[] outcomes)
    {
        var originals = PawnList.Parser.ParseFrom(raid.Pawns); var result = new PawnList();
        foreach (var outcome in report.Outcomes.Where(o => outcomes.Contains(o.Outcome)))
        {
            if (outcome.Index < 0 || outcome.Index >= originals.Pawns.Count) throw Invalid("custody-raid-outcome-index");
            var original = originals.Pawns[outcome.Index];
            if (outcome.Blob is null && original.Registry is null && outcome.Outcome == PawnOutcome.OutcomeCaptured) continue;
            if (outcome.Blob is null) throw Invalid("custody-raid-outcome-blob");
            var pawn = outcome.Blob.Clone();
            if (original.Registry is not null)
            {
                var registry = pawn.Registry?.Clone(); if (registry is null) throw Invalid("custody-raid-outcome-registry");
                registry.NonAuthoritativeMirror = original.Registry.NonAuthoritativeMirror;
                if (!registry.Equals(original.Registry) || pawn.KindDef != original.KindDef) throw Invalid("custody-raid-outcome-registry");
                pawn.Registry = original.Registry.Clone(); pawn.Registry.NonAuthoritativeMirror = false;
            }
            else if (pawn.Registry is not null) throw Invalid("custody-raid-outcome-unexpected-registry");
            result.Pawns.Add(pawn);
        }
        return result;
    }

    public static IReadOnlyList<CustodyRoute> RaidReportRoutes(Storage.Entities.Raid raid, RaidReport report)
    {
        var originals = PawnList.Parser.ParseFrom(raid.Pawns); var routes = new List<CustodyRoute>();
        byte[] body = report.ToByteArray(), digest = System.Security.Cryptography.SHA256.HashData(body);
        var requestKind = report.LiveSessionId > 0 && report.RaidId == 0 ? OrdinaryRequestKind.LiveReport : OrdinaryRequestKind.RaidReport;
        if (raid.PawnMode == (int)PawnMode.PawnReal)
        {
            foreach (var group in report.Outcomes.GroupBy(o => o.Outcome is PawnOutcome.OutcomeAlive or PawnOutcome.OutcomeFled ? 1 : o.Outcome == PawnOutcome.OutcomeCaptured ? 2 : 0))
            {
                var selected = new PawnList(); var selectedReport = report.Clone(); selectedReport.Outcomes.Clear();
                foreach (var item in group)
                    if (group.Key != 2 || originals.Pawns[item.Index].Registry is not null)
                    { selected.Pawns.Add(originals.Pawns[item.Index]); selectedReport.Outcomes.Add(item); }
                if (selected.Pawns.Count == 0) continue;
                PawnList? result = group.Key == 0 ? null : RaidOutcomePawns(raid, selectedReport, group.Select(o => o.Outcome).Distinct().ToArray());
                routes.Add(new(raid.AttackerPlayerId, group.Key == 0 ? 0 : group.Key == 1 ? raid.AttackerPlayerId : raid.TargetPlayerId,
                    selected, group.Key == 1 ? CustodyTransferKind.CustodyTransferRaidReturn : CustodyTransferKind.CustodyTransferRaidReport,
                    raid.Id, report.ClientRequestId, digest, true, SourcePayload: body, ResultPawns: result, SourceAuthorizer: raid.TargetPlayerId, RequestKind: requestKind));
            }
        }
        if (report.Captives is { Pawns.Count: > 0 }) routes.Add(new(raid.TargetPlayerId, raid.AttackerPlayerId, report.Captives,
            CustodyTransferKind.CustodyTransferRaidReport, raid.Id, report.ClientRequestId, digest, false, SourcePayload: body, SourceAuthorizer: raid.TargetPlayerId,
            RequestKind: requestKind));
        if (routes.Count == 0) routes.Add(new(raid.AttackerPlayerId, raid.TargetPlayerId, new PawnList(), CustodyTransferKind.CustodyTransferRaidReport,
            raid.Id, report.ClientRequestId, digest, false, SourceAuthorizer: raid.TargetPlayerId, RequestKind: requestKind));
        return routes;
    }

    private static async Task<bool> RaidReportSourceMatchesAsync(RimlinkDbContext db, Storage.Entities.Raid raid, CustodyTransferEvidence evidence, CancellationToken ct)
    {
        if (evidence.SourcePayload.Length > Rimlink.Protocol.Limits.MaxMessageBytes || raid.State != RaidStateDb.Resolved
            || evidence.SourceAuthorizerPlayerId != raid.TargetPlayerId || evidence.NonAuthoritativeMirror) return false;
        using var input = CodedInputStream.CreateWithLimits(new MemoryStream(evidence.SourcePayload.ToByteArray()), Rimlink.Protocol.Limits.MaxMessageBytes, 32);
        var report = RaidReport.Parser.ParseFrom(input);
        if (report.RaidId != raid.Id || report.ClientRequestId != evidence.ClientRequestId || evidence.SourceRequestIntent.Length != 32
            || !System.Security.Cryptography.SHA256.HashData(report.ToByteArray()).AsSpan().SequenceEqual(evidence.SourceRequestIntent.Span)
            || !await db.Transfers.AsNoTracking().AnyAsync(t => t.Kind == "raid-report" && t.FromPlayerId == raid.TargetPlayerId
                && t.ToPlayerId == raid.AttackerPlayerId && t.RequestTargetId == raid.Id && t.ClientRequestId == report.ClientRequestId
                && t.RequestIntent != null && t.RequestIntent.SequenceEqual(evidence.SourceRequestIntent.ToByteArray()), ct)) return false;
        PawnList expected;
        ParcelKindDb parcelKind;
        if (evidence.ActorPlayerId == raid.TargetPlayerId)
        {
            if (evidence.Kind != CustodyTransferKind.CustodyTransferRaidReport || evidence.RecipientPlayerId != raid.AttackerPlayerId
                || report.Captives is null || !report.Captives.Equals(evidence.Pawns)) return false;
            expected = report.Captives; parcelKind = ParcelKindDb.Captive;
        }
        else
        {
            if (evidence.ActorPlayerId != raid.AttackerPlayerId || raid.PawnMode != (int)PawnMode.PawnReal) return false;
            var originals = PawnList.Parser.ParseFrom(raid.Pawns); var chosen = new List<PawnOutcomeMsg>();
            foreach (var pawn in evidence.Pawns.Pawns)
            {
                int index = originals.Pawns.IndexOf(pawn); if (index < 0) return false;
                var outcome = report.Outcomes.SingleOrDefault(o => o.Index == index); if (outcome is null) return false;
                if (!await db.RaidOutcomes.AsNoTracking().AnyAsync(o => o.RaidId == raid.Id && o.Index == index && o.Outcome == (int)outcome.Outcome, ct)) return false;
                chosen.Add(outcome);
            }
            if (evidence.RecipientPlayerId == 0) return chosen.Count > 0 && chosen.All(o => o.Outcome == PawnOutcome.OutcomeDead) && evidence.ParcelIds.Count == 0;
            bool own = evidence.RecipientPlayerId == raid.AttackerPlayerId;
            if (!own && evidence.RecipientPlayerId != raid.TargetPlayerId || chosen.Any(o => own
                ? o.Outcome is not (PawnOutcome.OutcomeAlive or PawnOutcome.OutcomeFled) : o.Outcome != PawnOutcome.OutcomeCaptured)) return false;
            var selectedReport = report.Clone(); selectedReport.Outcomes.Clear(); selectedReport.Outcomes.Add(chosen);
            expected = RaidOutcomePawns(raid, selectedReport, own ? [PawnOutcome.OutcomeAlive, PawnOutcome.OutcomeFled] : [PawnOutcome.OutcomeCaptured]);
            if (evidence.ResultPawns is null || !evidence.ResultPawns.Equals(expected)) return false;
            parcelKind = own ? ParcelKindDb.RaidReturn : ParcelKindDb.Captive;
        }
        if (evidence.ParcelIds.Count != 1) return false;
        var parcel = await db.Parcels.AsNoTracking().SingleOrDefaultAsync(p => p.Id == evidence.ParcelIds[0], ct);
        return parcel is not null && parcel.Kind == parcelKind && parcel.ToPlayerId == evidence.RecipientPlayerId
            && parcel.Pawns.AsSpan().SequenceEqual(expected.ToByteArray());
    }
}
