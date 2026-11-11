using Grpc.Core;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.GuildCapture;
using Rimlink.Server.Application.Outposts;

namespace Rimlink.Server.Grpc;

internal sealed partial class GuildCaptureGrpcService(GuildOutpostService resources, OutpostAuthority authority,BattleControlTransport transport,BattleReadyOfferService offers,BattlePreparedViewService views) : Protocol.V1.GuildCapture.GuildCaptureBase
{
    private static void RequireGuildClient(ServerCallContext context)
    {
        var session = context.GetSession();
        if (session.GuildVersion != GuildResourceContracts.CurrentVersion)
            throw RpcErrors.With(StatusCode.FailedPrecondition, ErrorCode.RecoveryClientRequired, RefusalKeys.GuildClient,
                "guild resource client version required");
    }
    private void Ready(ServerCallContext context)
    {
        RequireGuildClient(context);
        try { authority.RequireRuntimeGuild(); }
        catch (OutpostEvidenceUnavailableException)
        { throw RpcErrors.Failed(ErrorCode.GuildUnavailable, RefusalKeys.GuildUnavailable, "guild product is unavailable"); }
    }
    public override async Task<BattleAttackerReadyReply> AttackerReady(BattleAttackerReadyRequest request,ServerCallContext context)
    {
        RequireGuildClient(context);
        try{return await resources.AttackerReadyAsync(context.GetSession(),request,context.CancellationToken);}
        catch(Exception error)when(error is OutpostEvidenceUnavailableException or InvalidDataException)
        {throw RpcErrors.Failed(ErrorCode.GuildUnavailable,RefusalKeys.GuildUnavailable,"guild battle readiness was refused");}
    }
    public override async Task<BattleControlGrantReply> ReconcileControl(BattleReconcileControlRequest request,ServerCallContext context)
    {
        RequireGuildClient(context);
        try{return await resources.ReconcileBattleControlAsync(context.GetSession(),request,context.CancellationToken);}
        catch(Exception error)when(error is OutpostEvidenceUnavailableException or InvalidDataException)
        {throw RpcErrors.Failed(ErrorCode.GuildUnavailable,RefusalKeys.GuildUnavailable,"guild battle control was refused");}
    }
    public override async Task<GuildDeliveryAuthorization> BeginDelivery(GuildDeliveryRequest request, ServerCallContext context)
    {
        Ready(context);
        try { return await resources.BeginDeliveryAsync(context.GetSession().PlayerId, request, context.CancellationToken); }
        catch (OutpostEvidenceUnavailableException)
        { throw RpcErrors.Failed(ErrorCode.GuildDeliveryInvalid, RefusalKeys.GuildDelivery, "guild delivery evidence was refused"); }
    }
    public override async Task<OutpostManifestPage> Manifest(GuildManifestReadRequest request, ServerCallContext context)
    {
        Ready(context);
        try { return await resources.ReadDeliveryManifestAsync(context.GetSession().PlayerId, request, context.CancellationToken); }
        catch (OutpostEvidenceUnavailableException)
        { throw RpcErrors.Failed(ErrorCode.GuildDeliveryInvalid, RefusalKeys.GuildDelivery, "guild delivery manifest was refused"); }
    }
}
