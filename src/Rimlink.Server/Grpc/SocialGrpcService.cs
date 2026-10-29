using Grpc.Core;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Social;

namespace Rimlink.Server.Grpc;

public sealed class SocialGrpcService(SocialService social) : Protocol.V1.Social.SocialBase
{
    public override async Task<BlockList> Block(PlayerRef request, ServerCallContext context) =>
        ToList(await social.Block(context.GetSession().PlayerId, request.PlayerId));

    public override async Task<BlockList> Unblock(PlayerRef request, ServerCallContext context) =>
        ToList(await social.Unblock(context.GetSession().PlayerId, request.PlayerId));

    public override async Task<BlockList> ListBlocks(Empty request, ServerCallContext context) =>
        ToList(await social.List(context.GetSession().PlayerId));

    public override Task<PlayerFlags> SetAcceptGifts(FlagRequest request, ServerCallContext context) =>
        social.SetAcceptGifts(context.GetSession().PlayerId, request.On);

    public override Task<PlayerFlags> SetTradesClosed(FlagRequest request, ServerCallContext context) =>
        social.SetTradesClosed(context.GetSession().PlayerId, request.On);

    private static BlockList ToList(List<PlayerRef> players)
    {
        var list = new BlockList();
        list.Players.AddRange(players);
        return list;
    }
}
