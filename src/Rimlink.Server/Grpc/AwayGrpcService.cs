using Grpc.Core;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Away;

namespace Rimlink.Server.Grpc;

public sealed class AwayGrpcService(AwaySummaryService summaries) : Protocol.V1.Away.AwayBase
{
    public override Task<AwaySummary> Summary(Empty request, ServerCallContext context) => summaries.Build(context.GetSession());
}
