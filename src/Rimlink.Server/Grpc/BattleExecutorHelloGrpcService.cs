using Grpc.Core;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Outposts;

namespace Rimlink.Server.Grpc;

internal sealed partial class BattleExecutorGrpcService
{
    public override Task<BattleExecutorHelloChallenge> HelloBegin(BattleExecutorHelloBindingV1 request,ServerCallContext context)
    {
        if(request.CalculateSize()>2048 || request.ExecutorId!=BattleExecutorAuthentication.Identity(context))throw Refused();
        return HelloCall(()=>hello.BeginAsync(request,BattleExecutorAuthentication.Generation(context),context.CancellationToken));
    }
    public override Task<BattleExecutorLeaseMessage> HelloComplete(BattleExecutorHelloComplete request,ServerCallContext context)
    {
        if(request.CalculateSize()>4096 || request.Binding?.ExecutorId!=BattleExecutorAuthentication.Identity(context))throw Refused();
        return HelloCall(()=>hello.CompleteAsync(request,BattleExecutorAuthentication.Generation(context),context.CancellationToken));
    }
    private static async Task<T> HelloCall<T>(Func<Task<T>> call)
    {
        try{return await call();}
        catch(Exception error)when(error is OutpostEvidenceUnavailableException or InvalidDataException or IOException)
        {throw RpcErrors.Failed(ErrorCode.GuildUnavailable,RefusalKeys.GuildUnavailable,"executor hello requires reconciliation");}
    }
}
