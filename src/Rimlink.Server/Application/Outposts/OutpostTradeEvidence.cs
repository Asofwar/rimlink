using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Rimlink.Protocol.V1;
using Rimlink.Server.Storage;
using Rimlink.Server.Storage.Entities;

namespace Rimlink.Server.Application.Outposts;

public sealed partial class PawnCustodyBridge
{
    private static async Task<bool> TradeSourceMatchesAsync(RimlinkDbContext db, CustodyTransferEvidence evidence, CancellationToken ct)
    {
        if (evidence.Kind is not (CustodyTransferKind.CustodyTransferTradeSide or CustodyTransferKind.CustodyTransferTradeReturn
            or CustodyTransferKind.CustodyTransferTradeFinish) || evidence.NonAuthoritativeMirror) return false;
        var trade = await db.Trades.AsNoTracking().SingleOrDefaultAsync(t => t.Id == evidence.SourceId, ct);
        if (trade is null || evidence.ActorPlayerId != trade.InitiatorPlayerId && evidence.ActorPlayerId != trade.PartnerPlayerId) return false;
        if (evidence.Kind == CustodyTransferKind.CustodyTransferTradeSide)
        {
            if (evidence.SourceRequestIntent.Length != 32 || evidence.ParcelIds.Count != 0) return false;
            var receipt = await db.Transfers.AsNoTracking().SingleOrDefaultAsync(t => t.FromPlayerId == evidence.ActorPlayerId
                && (t.Kind == "trade-open" || t.Kind == "trade-side") && t.ClientRequestId == evidence.ClientRequestId
                && t.RequestTargetId == evidence.SourceId, ct);
            return receipt?.RequestIntent is not null && receipt.RequestIntent.AsSpan().SequenceEqual(evidence.SourceRequestIntent.Span);
        }
        if (evidence.Kind == CustodyTransferKind.CustodyTransferTradeFinish && evidence.ParcelIds.Count == 0)
        {
            var confirmation = await db.Transfers.AsNoTracking().SingleOrDefaultAsync(t => t.FromPlayerId == evidence.ActorPlayerId
                && t.Kind == "trade-custody-confirm" && t.ClientRequestId == evidence.ClientRequestId && t.RequestTargetId == trade.Id, ct);
            return confirmation?.RequestIntent is not null && confirmation.RequestIntent.AsSpan().SequenceEqual(evidence.SourceRequestIntent.Span);
        }
        if (evidence.ParcelIds.Count != 1) return false;
        var parcel = await db.Parcels.AsNoTracking().SingleOrDefaultAsync(p => p.Id == evidence.ParcelIds[0], ct);
        if (parcel is null || parcel.ToPlayerId != evidence.RecipientPlayerId || !parcel.Pawns.AsSpan().SequenceEqual(evidence.Pawns.ToByteArray())) return false;
        if (evidence.Kind == CustodyTransferKind.CustodyTransferTradeFinish)
            return trade.Status == TradeStatusDb.Completed && parcel.Kind == ParcelKindDb.Trade && parcel.FromPlayerId == evidence.ActorPlayerId
                && evidence.RecipientPlayerId == (trade.InitiatorPlayerId == evidence.ActorPlayerId ? trade.PartnerPlayerId : trade.InitiatorPlayerId);
        if (evidence.RecipientPlayerId != evidence.ActorPlayerId || parcel.FromPlayerId is not null || parcel.Kind != ParcelKindDb.TradeReturn) return false;
        if (trade.Status is TradeStatusDb.Cancelled or TradeStatusDb.Expired) return true;
        var update = await db.Transfers.AsNoTracking().SingleOrDefaultAsync(t => t.FromPlayerId == evidence.ActorPlayerId && t.Kind == "trade-side"
            && t.ClientRequestId == evidence.ClientRequestId && t.RequestTargetId == trade.Id, ct);
        return update?.RequestIntent is not null && update.RequestIntent.AsSpan().SequenceEqual(evidence.SourceRequestIntent.Span);
    }

    internal static bool CustodyFrontierMatches(OutpostTransition transition, PawnCustodyRecord custody)
    {
        if (transition.WorldId != custody.WorldId || transition.RestoreBoundary != custody.RestoreBoundary) return false;
        if (transition.PlayerId == custody.PlayerId && transition.SaveGeneration == custody.SaveGeneration) return true;
        if (transition.Kind != "CustodyTransfer") return false;
        return Parts(CustodyTransferEvidence.Parser.ParseFrom(transition.Intent)).Any(p => p.ActorPlayerId == custody.PlayerId
            && p.RemovalSnapshot?.Scope?.SaveGeneration == custody.SaveGeneration
            && p.Pawns.Pawns.Any(pawn => pawn.Registry?.PawnCustodyId == custody.Id));
    }
}
