using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Rimlink.Protocol.V1;
using Rimlink.Server.Storage;
using Rimlink.Server.Storage.Entities;
using SessionRow = Rimlink.Server.Storage.Entities.LiveSession;

namespace Rimlink.Server.Application.Outposts;

public sealed partial class PawnCustodyBridge
{
    private static Storage.Entities.Raid VisitAsRaid(SessionRow session, byte[] original) => new()
    { Id = session.Id, AttackerPlayerId = session.GuestPlayerId, TargetPlayerId = session.HostPlayerId, PawnMode = (int)PawnMode.PawnReal, Pawns = original };

    public static RaidReport CompleteVisitReport(SessionRow session, RaidReport input)
    {
        var report = input.Clone(); var original = PawnList.Parser.ParseFrom(session.Pawns ?? []);
        var seen = new HashSet<int>();
        foreach (var outcome in report.Outcomes)
            if (outcome.Index < 0 || outcome.Index >= original.Pawns.Count || !seen.Add(outcome.Index)) throw Invalid("custody-live-outcome-index");
        for (int index = 0; index < original.Pawns.Count; index++)
            if (!seen.Contains(index)) report.Outcomes.Add(new PawnOutcomeMsg { Index = index, Outcome = PawnOutcome.OutcomeAlive, Blob = original.Pawns[index] });
        return report;
    }

    public static IReadOnlyList<CustodyRoute> VisitReportRoutes(SessionRow session, RaidReport report)
    {
        var fake = VisitAsRaid(session, session.Pawns ?? []);
        return RaidReportRoutes(fake, report).Select(r => r with { Kind = CustodyTransferKind.CustodyTransferLiveReturn }).ToArray();
    }

    public static PawnList VisitOutcomePawns(SessionRow session, RaidReport report, params PawnOutcome[] outcomes) =>
        RaidOutcomePawns(VisitAsRaid(session, session.Pawns ?? []), report, outcomes);

    private static async Task<bool> LiveSourceMatchesAsync(RimlinkDbContext db, CustodyTransferEvidence evidence, CancellationToken ct)
    {
        if (evidence.Kind is not (CustodyTransferKind.CustodyTransferLiveLaunch or CustodyTransferKind.CustodyTransferLiveReturn)
            || evidence.NonAuthoritativeMirror || evidence.SourcePayload.Length > Rimlink.Protocol.Limits.MaxMessageBytes) return false;
        var session = await db.LiveSessions.AsNoTracking().SingleOrDefaultAsync(s => s.Id == evidence.SourceId, ct);
        if (session is null || session.RaidId is not null || session.Kind != (int)LiveKind.LiveVisit || session.GuestPlayerId != evidence.ActorPlayerId) return false;
        if (evidence.Kind == CustodyTransferKind.CustodyTransferLiveLaunch)
        {
            var proposal = LiveProposal.Parser.ParseFrom(evidence.SourcePayload);
            return proposal.Kind == LiveKind.LiveVisit && proposal.HostPlayerId == session.HostPlayerId && proposal.ClientRequestId == evidence.ClientRequestId
                && session.ClientRequestId == evidence.ClientRequestId && proposal.Pawns.Equals(evidence.Pawns) && evidence.RecipientPlayerId == session.HostPlayerId
                && evidence.ParcelIds.Count == 0 && evidence.SourceRequestIntent.Equals(ByteString.CopyFrom(System.Security.Cryptography.SHA256.HashData(proposal.ToByteArray())))
                && (session.Pawns is null ? session.State is LiveStateDb.Ended or LiveStateDb.Fallback : session.Pawns.AsSpan().SequenceEqual(evidence.Pawns.ToByteArray()))
                && await db.Transfers.AsNoTracking().AnyAsync(t => t.Kind == "live-launch" && t.FromPlayerId == session.GuestPlayerId && t.ToPlayerId == session.HostPlayerId
                    && t.RequestTargetId == session.Id && t.ClientRequestId == evidence.ClientRequestId && t.RequestIntent != null
                    && t.RequestIntent.SequenceEqual(evidence.SourceRequestIntent.ToByteArray()), ct);
        }
        if (session.State is not (LiveStateDb.Ended or LiveStateDb.Fallback) || session.Pawns is not null) return false;
        var predecessor = await db.OutpostTransitions.AsNoTracking().SingleOrDefaultAsync(t => t.Id == evidence.SourceTransitionId, ct);
        if (predecessor?.Kind != "CustodyTransfer") return false;
        var launch = Parts(CustodyTransferEvidence.Parser.ParseFrom(predecessor.Intent)).SingleOrDefault(p => p.Kind == CustodyTransferKind.CustodyTransferLiveLaunch
            && p.SourceId == session.Id && p.ActorPlayerId == session.GuestPlayerId);
        if (launch is null || !launch.RemovalSnapshot.Equals(evidence.RemovalSnapshot)) return false;
        PawnList expected; ParcelKindDb kind;
        if (evidence.SourceAuthorizerPlayerId == 0)
        {
            if (evidence.RecipientPlayerId != session.GuestPlayerId || !evidence.Pawns.Equals(launch.Pawns) || !evidence.SourcePayload.Equals(launch.SourcePayload)) return false;
            expected = launch.Pawns; kind = ParcelKindDb.RaidReturn;
        }
        else
        {
            if (evidence.SourceAuthorizerPlayerId != session.HostPlayerId) return false;
            var report = RaidReport.Parser.ParseFrom(evidence.SourcePayload);
            if (report.LiveSessionId != session.Id || report.ClientRequestId != evidence.ClientRequestId
                || !evidence.SourceRequestIntent.Equals(ByteString.CopyFrom(System.Security.Cryptography.SHA256.HashData(report.ToByteArray())))
                || !await db.Transfers.AsNoTracking().AnyAsync(t => t.Kind == "live-visit" && t.FromPlayerId == session.HostPlayerId && t.ToPlayerId == session.GuestPlayerId
                    && t.RequestTargetId == session.Id && t.ClientRequestId == report.ClientRequestId && t.RequestIntent != null
                    && t.RequestIntent.SequenceEqual(evidence.SourceRequestIntent.ToByteArray()), ct)) return false;
            var selected = report.Clone(); selected.Outcomes.Clear();
            foreach (var pawn in evidence.Pawns.Pawns)
            {
                int index = launch.Pawns.Pawns.IndexOf(pawn); if (index < 0) return false;
                var outcome = report.Outcomes.SingleOrDefault(o => o.Index == index); if (outcome is null) return false;
                selected.Outcomes.Add(outcome);
            }
            if (evidence.RecipientPlayerId == 0) return selected.Outcomes.Count > 0 && selected.Outcomes.All(o => o.Outcome == PawnOutcome.OutcomeDead) && evidence.ParcelIds.Count == 0;
            bool own = evidence.RecipientPlayerId == session.GuestPlayerId;
            if (!own && evidence.RecipientPlayerId != session.HostPlayerId || selected.Outcomes.Any(o => own
                ? o.Outcome is not (PawnOutcome.OutcomeAlive or PawnOutcome.OutcomeFled) : o.Outcome != PawnOutcome.OutcomeCaptured)) return false;
            expected = RaidOutcomePawns(VisitAsRaid(session, launch.Pawns.ToByteArray()), selected,
                own ? [PawnOutcome.OutcomeAlive, PawnOutcome.OutcomeFled] : [PawnOutcome.OutcomeCaptured]);
            if (!expected.Equals(evidence.ResultPawns)) return false;
            kind = own ? ParcelKindDb.RaidReturn : ParcelKindDb.Captive;
        }
        if (evidence.ParcelIds.Count != 1) return false;
        var parcel = await db.Parcels.AsNoTracking().SingleOrDefaultAsync(p => p.Id == evidence.ParcelIds[0], ct);
        return parcel is not null && parcel.Kind == kind && parcel.ToPlayerId == evidence.RecipientPlayerId && parcel.Pawns.AsSpan().SequenceEqual(expected.ToByteArray());
    }
}
