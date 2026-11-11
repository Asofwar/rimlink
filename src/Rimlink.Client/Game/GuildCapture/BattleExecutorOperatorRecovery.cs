using Google.Protobuf;
using Grpc.Core;
using Rimlink.Client.GuildCapture;
using Rimlink.Client.UI;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Verse;

namespace Rimlink.Client.Game.GuildCapture;

internal sealed partial class BattleExecutorOperator
{
    private async Task Register(BattleExecutorLane lane)
    {
        while(true)
        {
            try{await connection.Register(await Binding(lane),default);return;}
            catch(RpcException error)when(Retryable(error) || error.StatusCode==StatusCode.FailedPrecondition){await Task.Delay(1500);}
        }
    }
    private Task<BattleExecutorHelloBindingV1> Binding(BattleExecutorLane lane,bool startPending=false)=>MainThread.Get(()=>
    {
        var result=new BattleExecutorHelloBindingV1{Version=1,ExecutorId=settings.ExecutorId,RuntimeInstanceId=runtimeId,ProfileDigest=profile.Digest,
            InventorySha256=profile.Profile.Inventory.Sha256,ExecutorBuildSha256=profile.Profile.ExecutorBuildSha256,Lane=lane,
            WorldInstanceId=settings.WorldInstanceId,RestoreBoundary=settings.RestoreBoundary};
        var held=state;
        if(held is null)
        {
            RequireEntry();if(BattleGameContext.Active)throw Invalid();result.Entry=new(){Version=1,AtEntry=true};return result;
        }
        var assignment=held.Assignment;result.BattleId=assignment.Scope.BattleId;result.Attempt=assignment.Scope.Attempt;result.Fence=assignment.Scope.Fence;result.AssignmentTransitionId=assignment.TransitionId;
        if(held.Start is{} start)result.StartDigest=start.ManifestDigest;
        else if (held.AbortTerminal is { } abort) result.StartDigest = abort.AbortedStartDigest;
        else if(startPending && !held.PreparedManifest.IsEmpty)result.StartDigest=ByteString.CopyFrom(BattleManifestContracts.Digest(BattleDocumentKind.PreparedManifest,held.PreparedManifest.Span));
        if(lane==BattleExecutorLane.RecoveryDelivery)return result;
        if(LongEventHandler.AnyEventNowOrWaiting)throw new BattleExecutorLoadingException();
        if(controller is not null)
            result.RetainedContext=controller.RetainedContext(pump?.PendingCuts??[],callbacks+(pump?.PendingCallbacks??0));
        else if(held.Stage is BattleExecutorCheckpointStage.AtEntry or BattleExecutorCheckpointStage.Cleaned)
        {
            RequireEntry();result.RetainedContext=new(){Version=1,Phase=BattleExecutorRetainedPhase.Entry,StartNonce=assignment.StartNonce,AssignmentRejected=true};
        }
        else
        {
            bool loaded=Current.ProgramState==ProgramState.Playing && Current.Game is not null;
            result.RetainedContext=new(){Version=1,Phase=loaded?BattleExecutorRetainedPhase.InitialLoaded:BattleExecutorRetainedPhase.PreparePending,StartNonce=assignment.StartNonce,
                GameLoaded=loaded,CurrentTick=loaded?Find.TickManager.TicksGame:0,PendingCallbacks=callbacks};
            if(startPending)
            {
                if(!loaded || held.PreparedManifest.IsEmpty || !BattleGameContext.IsBattleGame)throw Invalid();
                var prepared=BattleManifestContracts.ReadPrepared(held.PreparedManifest.Span);scenario!.VerifyInitial();
                if(Find.TickManager.TicksGame!=prepared.InitialTick)throw Invalid();
                result.RetainedContext.Phase=BattleExecutorRetainedPhase.StartAcknowledgementPending;
                result.RetainedContext.StartRequestId=StartRequest(held.PreparedManifest).ClientRequestId;result.RetainedContext.InitialSnapshot=prepared.InitialSnapshot.Clone();
            }
        }
        return result;
    });
    private async Task EnsureTransport()
    {
        try{await connection.Heartbeat(state is not null && state.Stage is not(BattleExecutorCheckpointStage.AtEntry or BattleExecutorCheckpointStage.Cleaned),default);}
        catch(RpcException error)when(Retryable(error) || error.StatusCode==StatusCode.FailedPrecondition){await RecoverTransport();}
    }
    private async Task RecoverTransport(CancellationToken ct=default)
    {
        await reconnect.WaitAsync(ct);
        try
        {
            while(true)
            {
                ct.ThrowIfCancellationRequested();
                if (state is null && File.Exists(settings.RestoreCleanupPath())) throw new RestoreCleanupRequested();
                // A publishing cut does not revoke the lease or transfer its pending assignment to a new hello.
                try{await connection.Heartbeat(state is not null && state.Stage is not(BattleExecutorCheckpointStage.AtEntry or BattleExecutorCheckpointStage.Cleaned),ct);return;}
                catch(RpcException error)when(Retryable(error)){await Task.Delay(1000,ct);continue;}
                catch(RpcException error)when(error.StatusCode==StatusCode.FailedPrecondition){}
                try
                {
                    var lane=connection.Lease?.Binding?.Lane??BattleExecutorLane.Execution;
                    await connection.Reconnect(await Binding(lane),ct);return;
                }
                catch(RpcException error)when(error.StatusCode==StatusCode.FailedPrecondition)
                {
                    if(state is{Stage:BattleExecutorCheckpointStage.Prepared})
                    {
                        try{await connection.Reconnect(await Binding(BattleExecutorLane.Execution,startPending:true),ct);return;}
                        catch(RpcException refused)when(Retryable(refused) || refused.StatusCode==StatusCode.FailedPrecondition){}
                    }
                    if(state is{} held && held.Stage is BattleExecutorCheckpointStage.FinalCaptured or BattleExecutorCheckpointStage.Applied or BattleExecutorCheckpointStage.AtEntry)
                    {
                        var courier=new BattleExecutorConnection(settings,settings.ReadCredential());
                        try{await courier.Register(await Binding(BattleExecutorLane.RecoveryDelivery),ct);connection=courier;return;}
                        catch{courier.Dispose();throw;}
                    }
                }
                catch(RpcException error)when(Retryable(error)){}
                catch(BattleExecutorLoadingException){}
                await Task.Delay(1000,ct);
            }
        }
        finally{reconnect.Release();}
    }
    private async Task<T> Retry<T>(Func<Task<T>> action,bool refused=false)
    {
        while(true)
        {
            try{return await action();}
            catch(RpcException error)when(Retryable(error) || refused && error.StatusCode==StatusCode.FailedPrecondition)
            {await RecoverTransport();await Task.Delay(1000);}
        }
    }
    private void StartHeartbeat()
    {
        heartbeatLife=new();var token=heartbeatLife.Token;
        heartbeat=Task.Run(async()=>
        {
            try
            {
                while(true)
                {
                    await Task.Delay(10000,token);
                    try{await connection.Heartbeat(state is not null,token);}
                    catch(RpcException error)when(Retryable(error) || error.StatusCode==StatusCode.FailedPrecondition)
                    {
                        await MainThread.Get(()=>{controller?.TransportFailed();return true;});await RecoverTransport(token);
                    }
                }
            }
            catch(OperationCanceledException)when(token.IsCancellationRequested){}
            catch(RpcException error)when(token.IsCancellationRequested && error.StatusCode==StatusCode.Cancelled){}
            catch(RestoreCleanupRequested){}
        });
    }
    private async Task StopHeartbeat()
    {heartbeatLife.Cancel();await heartbeat;}
    private sealed class BattleExecutorLoadingException:Exception;
    private sealed class RestoreCleanupRequested:Exception;
    private static bool Retryable(RpcException error)=>error.StatusCode is StatusCode.Unavailable or StatusCode.DeadlineExceeded or StatusCode.ResourceExhausted;
}
