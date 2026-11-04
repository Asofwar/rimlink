using Google.Protobuf;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;

namespace Rimlink.Server.Application.Outposts;

public sealed partial class PawnCustodyBridge
{
    private static OrdinaryOutgoingCapture RequireOrdinaryCapture(SaveRecoveryData recovery, OrdinaryRequestKind kind, string requestId, ByteString intent)
    {
        if (!recovery.Outposts.Present || kind == OrdinaryRequestKind.Unspecified || intent.Length != 32)
            throw Invalid("custody-ordinary-capture-required");
        OrdinaryOutgoingCapture[] matches;
        try
        {
            matches = recovery.Outposts.Metadata.OrdinaryOutgoing.Where(c => OrdinaryCapture.Matches(c, kind, requestId, intent.Span)).ToArray();
        }
        catch (InvalidDataException) { throw Invalid("custody-ordinary-capture-invalid"); }
        if (matches.Length != 1 || !recovery.Outposts.Metadata.Scope.Equals(matches[0].Scope)
            || !recovery.Removed.Contains(matches[0].LocalOperationId) || recovery.Returned.Contains(matches[0].LocalOperationId))
            throw Invalid("custody-ordinary-removal-receipt");
        return matches[0];
    }

    private static bool OrdinaryRouteMatches(CustodyTransferKind route, OrdinaryRequestKind kind) => route switch
    {
        CustodyTransferKind.CustodyTransferSend => kind == OrdinaryRequestKind.TransferSend,
        CustodyTransferKind.CustodyTransferMarketPost => kind == OrdinaryRequestKind.MarketPost,
        CustodyTransferKind.CustodyTransferTradeSide => kind is OrdinaryRequestKind.TradeOpen or OrdinaryRequestKind.TradeSide,
        CustodyTransferKind.CustodyTransferRaidLaunch => kind == OrdinaryRequestKind.RaidLaunch,
        CustodyTransferKind.CustodyTransferLiveLaunch => kind == OrdinaryRequestKind.LivePropose,
        CustodyTransferKind.CustodyTransferRaidReport => kind == OrdinaryRequestKind.RaidReport,
        CustodyTransferKind.CustodyTransferLiveReturn => kind == OrdinaryRequestKind.LiveReport,
        _ => false
    };
}
