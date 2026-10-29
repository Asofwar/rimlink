using Grpc.Core;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Trades;

namespace Rimlink.Server.Grpc;

public sealed class TradesGrpcService(TradeService trades) : Protocol.V1.Trades.TradesBase
{
    public override Task<Trade> Open(TradeOpenRequest request, ServerCallContext context) =>
        trades.Open(context.GetSession().PlayerId, request);

    public override Task<Trade> SetSide(TradeSideRequest request, ServerCallContext context) =>
        trades.SetSide(context.GetSession().PlayerId, request);

    public override Task<Trade> Confirm(TradeConfirmRequest request, ServerCallContext context) =>
        trades.Confirm(context.GetSession().PlayerId, request.TradeId, request.Revision);

    public override Task<Trade> Cancel(TradeRef request, ServerCallContext context) =>
        trades.Cancel(context.GetSession().PlayerId, request.TradeId);

    public override Task<TradeList> List(Empty request, ServerCallContext context) =>
        trades.List(context.GetSession().PlayerId);
}
