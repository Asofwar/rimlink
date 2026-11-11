using Grpc.Core;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.GuildCapture;
using Rimlink.Server.Application.Outposts;

namespace Rimlink.Server.Grpc;

internal sealed partial class BattleExecutorGrpcService
{
    public override Task<BattleRuntimeStartedReply> RuntimeStarted(BattleRuntimeStartedRequest request,ServerCallContext context)
    {
        if(request.CalculateSize()>4096 || request.Executor?.ExecutorId!=BattleExecutorAuthentication.Identity(context))throw Refused();
        return TransportCall(()=>transport.RuntimeStartedAsync(request,context.CancellationToken));
    }
    public override Task<BattleRunningWork> PollRunning(BattleExecutorRunningRequest request,ServerCallContext context)
    {
        if(request.CalculateSize()>2048+BattleManifestContracts.RunningViewBytes || request.View.Length>BattleManifestContracts.RunningViewBytes
            || request.Executor?.ExecutorId!=BattleExecutorAuthentication.Identity(context))throw Refused();
        return TransportCall(()=>transport.PollAsync(BattleResultStaging.Lease(request.Executor),request.Scope,context.CancellationToken,request.View));
    }
    public override Task<BattleTransportPosition> AcknowledgeCommands(BattleExecutorCommandsAck request,ServerCallContext context)
    {
        if(request.CalculateSize()>16384 || request.Executor?.ExecutorId!=BattleExecutorAuthentication.Identity(context))throw Refused();
        return TransportCall(()=>transport.AcknowledgeAsync(request,context.CancellationToken));
    }
    private static async Task<T> TransportCall<T>(Func<Task<T>> call)
    {
        try{return await call();}
        catch(Exception error)when(error is OutpostEvidenceUnavailableException or InvalidDataException or IOException)
        {throw RpcErrors.Failed(ErrorCode.GuildUnavailable,RefusalKeys.GuildUnavailable,"battle transport requires reconciliation");}
    }
}
