using Grpc.Core;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Outposts;

namespace Rimlink.Server.Grpc;

internal sealed partial class GuildCaptureGrpcService
{
    public override Task<BattleControlChannelReply> AttachControl(BattleAttachControlRequest request,ServerCallContext context)=>ControlCall(context,
        ()=>transport.AttachAsync(context.GetSession(),context.GetHttpContext().Connection.Id,request,context.CancellationToken));
    public override Task<BattleSubmitCommandReply> SubmitCommand(BattleSubmitCommandRequest request,ServerCallContext context)=>ControlCall(context,
        ()=>transport.SubmitAsync(context.GetSession(),context.GetHttpContext().Connection.Id,request,context.CancellationToken));
    public override Task<BattleTransportPosition> ControlHeartbeat(BattleControlAccess request,ServerCallContext context)=>ControlCall(context,
        ()=>transport.HeartbeatAsync(context.GetSession(),context.GetHttpContext().Connection.Id,request,context.CancellationToken));
    public override Task<BattleRunningViewReply> ReadRunningView(BattleRunningViewRequest request,ServerCallContext context)=>ControlCall(context,
        ()=>transport.ReadViewAsync(context.GetSession(),context.GetHttpContext().Connection.Id,request,context.CancellationToken));
    public override Task<BattleCompletedViewReply> ReadCompletedView(BattleCompletedViewRequest request,ServerCallContext context)=>ControlCall(context,
        ()=>transport.ReadCompletedViewAsync(context.GetSession(),request,context.CancellationToken));
    private async Task<T> ControlCall<T>(ServerCallContext context,Func<Task<T>> call)
    {
        RequireGuildClient(context);
        try{return await call();}
        catch(Exception error)when(error is OutpostEvidenceUnavailableException or InvalidDataException or IOException)
        {throw RpcErrors.Failed(ErrorCode.GuildUnavailable,RefusalKeys.GuildUnavailable,"battle control requires reconciliation");}
    }
}
