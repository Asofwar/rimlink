using Grpc.Core;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Application.Sessions;

namespace Rimlink.Server.Grpc;

public sealed class OutpostsGrpcService(OutpostAssignmentService assignments, OutpostService outposts, OutpostParcels parcels, OrdinaryReturnService ordinary, OutpostFeature feature) : Protocol.V1.Outposts.OutpostsBase
{
    private Session Ready(ServerCallContext context)
    {
        var session = context.GetSession(); OutpostRefusalKeys.RequireReady(session, feature); return session;
    }
    public override Task<OrdinaryOperationReply> OrdinaryOperation(OrdinaryOperationQuery request, ServerCallContext context) =>
        ordinary.ResolveAsync(Ready(context).PlayerId, request, context.CancellationToken);
    public override Task<OrdinaryOperationReply> BeginOrdinaryReturn(OrdinaryCancelRequest request, ServerCallContext context) =>
        ordinary.BeginReturnAsync(Ready(context).PlayerId, request, context.CancellationToken);
    public override Task<OutpostStateReply> State(OutpostStateQuery request, ServerCallContext context) =>
        outposts.StateAsync(Ready(context).PlayerId, request, context.CancellationToken);
    public override Task<OutpostOperationReply> Mutate(OutpostMutationRequest request, ServerCallContext context) =>
        outposts.MutateAsync(Ready(context).PlayerId, request, context.CancellationToken);
    public override Task<OutpostDeliveryReply> BeginDelivery(OutpostDeliveryRequest request, ServerCallContext context) =>
        parcels.BeginDeliveryAsync(Ready(context).PlayerId, request, context.CancellationToken);
    public override Task<OutpostManifestPage> Manifest(OutpostManifestReadRequest request, ServerCallContext context) =>
        outposts.ReadManifestAsync(Ready(context).PlayerId, request, context.CancellationToken);
    public override Task<OutpostOperationReply> Prepare(OutpostPrepareRequest request, ServerCallContext context) =>
        assignments.PrepareAsync(Ready(context).PlayerId, request, context.CancellationToken);
    public override Task<OutpostOperationReply> Commit(OutpostProofRequest request, ServerCallContext context) =>
        assignments.CommitAsync(Ready(context).PlayerId, request, context.CancellationToken);
    public override Task<OutpostOperationReply> Cancel(OutpostCancelRequest request, ServerCallContext context) =>
        assignments.CancelAsync(Ready(context).PlayerId, request, context.CancellationToken);
    public override Task<OutpostOperationReply> Operation(OutpostOperationQuery request, ServerCallContext context) =>
        assignments.OperationAsync(Ready(context).PlayerId, request, context.CancellationToken);
}
