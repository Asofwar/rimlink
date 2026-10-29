using Grpc.Core;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Economy;

namespace Rimlink.Server.Grpc;

public sealed class MarketGrpcService(MarketService market, MailboxService mailbox) : Protocol.V1.Market.MarketBase
{
    public override Task<LotList> List(MarketQuery request, ServerCallContext context) => market.List(request, context.GetSession().PlayerId);

    public override async Task<Lot> Post(PostRequest request, ServerCallContext context)
    {
        var lot = await market.Post(context.GetSession().PlayerId, request);
        return MarketService.ToProto(lot, null);
    }

    public override async Task<Parcel> Withdraw(LotRef request, ServerCallContext context) =>
        await mailbox.ToProto(await market.Withdraw(context.GetSession().PlayerId, request.LotId));

    public override async Task<Parcel> Buy(LotRef request, ServerCallContext context) =>
        await mailbox.ToProto(await market.Buy(context.GetSession().PlayerId, request.LotId));
}
