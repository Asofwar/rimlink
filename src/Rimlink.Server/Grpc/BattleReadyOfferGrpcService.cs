using Grpc.Core;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Outposts;

namespace Rimlink.Server.Grpc;

internal sealed partial class GuildCaptureGrpcService
{
    public override Task<BattleReadyOfferReply> ReadReadyOffer(BattleReadyOfferRequest request,ServerCallContext context)=>OfferCall(context,
        ()=>offers.ReadAsync(context.GetSession(),request,context.CancellationToken));
    public override Task<BattleReadyOfferAckReply> AcknowledgeReadyOffer(BattleReadyOfferAckRequest request,ServerCallContext context)=>OfferCall(context,
        ()=>offers.AcknowledgeAsync(context.GetSession(),request,context.CancellationToken));
    private async Task<T> OfferCall<T>(ServerCallContext context,Func<Task<T>> call)
    {
        // The owning service validates readiness under its barrier, after any pending publisher.
        RequireGuildClient(context);try{return await call();}
        catch(Exception error)when(error is OutpostEvidenceUnavailableException or InvalidDataException or IOException)
        {throw RpcErrors.Failed(ErrorCode.GuildUnavailable,RefusalKeys.GuildUnavailable,"battle readiness offer requires reconciliation");}
    }
}
