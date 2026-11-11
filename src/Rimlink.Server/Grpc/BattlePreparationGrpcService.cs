using Grpc.Core;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Outposts;

namespace Rimlink.Server.Grpc;

internal sealed partial class BattleExecutorGrpcService
{
    public override async Task<BattlePreparationBasisReply> ReadPreparation(BattlePreparationBasisRequest request,ServerCallContext context)
    {
        if(request.Executor?.ExecutorId!=BattleExecutorAuthentication.Identity(context))throw Refused();
        try{return await prepared.ReadBasisAsync(request,context.CancellationToken);}
        catch(OutpostEvidenceUnavailableException){throw RpcErrors.Failed(ErrorCode.GuildUnavailable,RefusalKeys.GuildUnavailable,"preparation basis is unavailable or requires reconciliation");}
        catch(InvalidDataException){throw Refused();}
        catch(IOException){throw RpcErrors.Failed(ErrorCode.GuildUnavailable,RefusalKeys.GuildUnavailable,"preparation evidence storage is unavailable");}
    }
}
