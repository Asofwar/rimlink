using Grpc.Core;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Auctions;

namespace Rimlink.Server.Grpc;

public sealed class AuctionsGrpcService(AuctionService auctions) : Protocol.V1.Auctions.AuctionsBase
{
    public override Task<AuctionList> List(AuctionQuery request, ServerCallContext context) => auctions.List(context.GetSession().PlayerId, request);
    public override Task<Auction> Post(AuctionPostRequest request, ServerCallContext context) => auctions.Post(context.GetSession().PlayerId, request);
    public override Task<Auction> Bid(AuctionBidRequest request, ServerCallContext context) => auctions.Bid(context.GetSession().PlayerId, request);
    public override Task<Auction> Cancel(AuctionCancelRequest request, ServerCallContext context) => auctions.Cancel(context.GetSession().PlayerId, request);
}
