using Grpc.Core;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Application.Sessions;

namespace Rimlink.Server.Grpc;

public sealed class OutpostsGrpcService(OutpostAssignmentService assignments, OutpostFeature feature) : Protocol.V1.Outposts.OutpostsBase
{
    private Session Ready(ServerCallContext context)
    {
        var session = context.GetSession(); OutpostRefusalKeys.RequireReady(session, feature); return session;
    }
    public override Task<OutpostOperationReply> Prepare(OutpostPrepareRequest request, ServerCallContext context) =>
        assignments.PrepareAsync(Ready(context).PlayerId, request, context.CancellationToken);
    public override Task<OutpostOperationReply> Commit(OutpostProofRequest request, ServerCallContext context) =>
        assignments.CommitAsync(Ready(context).PlayerId, request, context.CancellationToken);
    public override Task<OutpostOperationReply> Cancel(OutpostCancelRequest request, ServerCallContext context) =>
        assignments.CancelAsync(Ready(context).PlayerId, request, context.CancellationToken);
    public override Task<OutpostOperationReply> Operation(OutpostOperationQuery request, ServerCallContext context) =>
        assignments.OperationAsync(Ready(context).PlayerId, request, context.CancellationToken);
}
