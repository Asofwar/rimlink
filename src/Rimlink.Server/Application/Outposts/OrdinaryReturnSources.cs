using Google.Protobuf;
using Grpc.Core;
using TransferRow = Rimlink.Server.Storage.Entities.Transfer;
using Microsoft.EntityFrameworkCore;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Rimlink.Server.Storage;
using Rimlink.Server.Storage.Entities;
using static Rimlink.Server.Application.Outposts.OutpostAssignmentService;

namespace Rimlink.Server.Application.Outposts;

public sealed partial class OrdinaryReturnService
{
    // Read retained business receipts, not active UI lists. Called again inside the negative write transaction.
    internal static async Task<OrdinaryOperationReply?> SourceAsync(RimlinkDbContext db, OrdinaryCancelRequest request, CancellationToken ct)
    {
        long actor = request.Scope.OwnerPlayerId; string key = request.Capture.ClientRequestId;
        ByteString digest = request.Capture.RequestIntentSha256;
        switch (Original(request))
        {
            case PostRequest:
                var lot = await db.Lots.AsNoTracking().SingleOrDefaultAsync(l => l.SellerPlayerId == actor && l.ClientRequestId == key, ct);
                return lot is null ? null : Applied(lot.Id, lot.RequestIntent, digest);
            case TransferRequest:
                var parcel = await db.Parcels.AsNoTracking().SingleOrDefaultAsync(p => p.RequestActorId == actor && p.RequestKind == "transfer.send" && p.ClientRequestId == key, ct);
                return parcel is null ? null : Applied(parcel.Id, parcel.RequestIntent, digest);
            case TradeOpenRequest open:
                var trade = await db.Trades.AsNoTracking().SingleOrDefaultAsync(t => t.InitiatorPlayerId == actor && t.ClientRequestId == key, ct);
                var opening = await SourceReceipt(db, actor, key, "trade-open", ct);
                if (trade is null && opening is null) return null;
                if (trade is null || opening is null || opening.RequestTargetId != trade.Id || opening.ToPlayerId != open.PartnerId
                    || trade.PartnerPlayerId != open.PartnerId) throw SourceUnavailable();
                Applied(trade.Id, trade.RequestIntent, digest);
                return Applied(trade.Id, opening.RequestIntent, digest);
            case TradeSideRequest side:
                var change = await SourceReceipt(db, actor, key, "trade-side", ct);
                if (change is null) return null;
                var changed = await db.Trades.AsNoTracking().SingleOrDefaultAsync(t => t.Id == side.TradeId, ct);
                if (changed is null || change.RequestTargetId != changed.Id || actor != changed.InitiatorPlayerId && actor != changed.PartnerPlayerId
                    || change.ToPlayerId != (actor == changed.InitiatorPlayerId ? changed.PartnerPlayerId : changed.InitiatorPlayerId)) throw SourceUnavailable();
                return Applied(changed.Id, change.RequestIntent, digest);
            case LaunchRequest launch:
                return await RaidSource(db, actor, key, launch, digest, ct);
            case LiveProposal proposal:
                var session = await db.LiveSessions.AsNoTracking().SingleOrDefaultAsync(s => s.GuestPlayerId == actor && s.ClientRequestId == key, ct);
                var entry = await SourceReceipt(db, actor, key, "live-launch", ct);
                if (session is null && entry is null)
                    return request.Capture.DerivedRoute is { } derived
                        ? await RaidSource(db, actor, derived.ClientRequestId, OrdinaryCapture.DerivedLiveRaid(proposal), derived.RequestIntentSha256, ct) : null;
                if (session is null || entry is null || entry.RequestTargetId != session.Id || entry.ToPlayerId != proposal.HostPlayerId
                    || session.HostPlayerId != proposal.HostPlayerId || session.Kind != (int)proposal.Kind) throw SourceUnavailable();
                Applied(session.Id, entry.RequestIntent, digest);
                if (proposal.Kind == LiveKind.LiveRaid)
                {
                    if (request.Capture.DerivedRoute is not { } mapped || session.RaidId is not { } raidId) throw SourceUnavailable();
                    var raid = await RaidSource(db, actor, mapped.ClientRequestId, OrdinaryCapture.DerivedLiveRaid(proposal), mapped.RequestIntentSha256, ct);
                    if (raid?.SourceId != raidId) throw SourceUnavailable();
                }
                return new() { Status = OrdinaryOperationStatus.Applied, SourceId = session.Id };
            case RaidReport report when request.Capture.RequestKind == OrdinaryRequestKind.RaidReport:
                var reportReceipt = await SourceReceipt(db, actor, key, "raid-report", ct);
                if (reportReceipt is null) return null;
                var reported = await db.Raids.AsNoTracking().SingleOrDefaultAsync(r => r.Id == report.RaidId, ct);
                if (reported is null || reportReceipt.RequestTargetId != reported.Id || reported.TargetPlayerId != actor
                    || reportReceipt.ToPlayerId != reported.AttackerPlayerId || reported.State != RaidStateDb.Resolved) throw SourceUnavailable();
                return Applied(reported.Id, reportReceipt.RequestIntent, digest);
            case RaidReport report:
                var visitReceipt = await SourceReceipt(db, actor, key, "live-visit", ct);
                if (visitReceipt is null) return null;
                var visited = await db.LiveSessions.AsNoTracking().SingleOrDefaultAsync(s => s.Id == report.LiveSessionId, ct);
                if (visited is null || visitReceipt.RequestTargetId != visited.Id || visited.HostPlayerId != actor
                    || visitReceipt.ToPlayerId != visited.GuestPlayerId || visited.Kind != (int)LiveKind.LiveVisit || visited.State != LiveStateDb.Ended)
                    throw SourceUnavailable();
                return Applied(visited.Id, visitReceipt.RequestIntent, digest);
            default: throw SourceUnavailable();
        }
    }

    private static async Task<OrdinaryOperationReply?> RaidSource(RimlinkDbContext db, long actor, string key,
        LaunchRequest request, ByteString digest, CancellationToken ct)
    {
        var raid = await db.Raids.AsNoTracking().SingleOrDefaultAsync(r => r.AttackerPlayerId == actor && r.ClientRequestId == key, ct);
        var receipt = await SourceReceipt(db, actor, key, "raid", ct);
        if (raid is null && receipt is null) return null;
        if (raid is null || receipt is null || receipt.RequestTargetId != raid.Id || receipt.ToPlayerId != request.TargetPlayerId
            || raid.TargetPlayerId != request.TargetPlayerId || raid.PawnMode != (int)request.PawnMode
            || raid.Timing != (int)request.Timing || raid.Arrival != (int)request.Arrival) throw SourceUnavailable();
        return Applied(raid.Id, receipt.RequestIntent, digest);
    }

    private static async Task<TransferRow?> SourceReceipt(RimlinkDbContext db, long actor, string key, string kind, CancellationToken ct)
    {
        var rows = await db.Transfers.AsNoTracking().Where(t => t.FromPlayerId == actor && t.Kind == kind && t.ClientRequestId == key).Take(2).ToListAsync(ct);
        if (rows.Count > 1) throw SourceUnavailable();
        return rows.SingleOrDefault();
    }

    private static OrdinaryOperationReply Applied(long id, byte[]? actual, ByteString expected)
    {
        if (actual is null || actual.Length != 32 || !actual.AsSpan().SequenceEqual(expected.Span))
            throw Refuse(ErrorCode.RequestIntentMismatch, "ordinary-source-intent-mismatch");
        return new() { Status = OrdinaryOperationStatus.Applied, SourceId = id };
    }

    private static RpcException SourceUnavailable() => Refuse(ErrorCode.RecoveryRequired, "ordinary-source-history-unavailable");
}
