using Grpc.Core;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Influence;

namespace Rimlink.Server.Grpc;

public sealed class InfluenceGrpcService(InfluenceService influence) : Protocol.V1.Influence.InfluenceBase
{
    public override Task<InfluenceSnapshot> Get(Empty request, ServerCallContext context) => influence.GetSnapshot(context.CancellationToken);
    public override Task<InfluenceSetting> GetSetting(InfluenceTarget request, ServerCallContext context) => influence.GetSetting(context.GetSession().PlayerId, request, context.CancellationToken);
    public override Task<InfluenceReceipt> Mutate(InfluenceMutation request, ServerCallContext context) => influence.Mutate(context.GetSession().PlayerId, request, context.CancellationToken);
}
