using Grpc.Core;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Social;

namespace Rimlink.Server.Grpc;

public sealed class CommunityGrpcService(AllianceService alliances) : Community.CommunityBase
{
    public override Task<CommunityState> Get(Empty request, ServerCallContext context) => alliances.Get(context.GetSession().PlayerId, context.CancellationToken);
    public override Task<PublicProfile> Profile(PlayerRef request, ServerCallContext context) => alliances.Profile(request.PlayerId, context.CancellationToken);
    public override Task<PublicProfiles> Directory(Empty request, ServerCallContext context) => alliances.Directory(context.CancellationToken);
    public override Task<CommunityReceipt> Mutate(CommunityRequest request, ServerCallContext context) => alliances.Mutate(context.GetSession().PlayerId, request, context.CancellationToken);
}
