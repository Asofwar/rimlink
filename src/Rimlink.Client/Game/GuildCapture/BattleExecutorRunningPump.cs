using System.Diagnostics;
using Google.Protobuf;
using Grpc.Core;
using Rimlink.Client.GuildCapture;
using Rimlink.Client.UI;
using Rimlink.Protocol.V1;

namespace Rimlink.Client.Game.GuildCapture;

// The operator startup owns this explicit loop; normal player login never starts an executor connection.
internal sealed class BattleExecutorRunningPump(Func<BattleExecutorConnection> connectionFor,BattleTickController controller,BattleScopeMessage scope,Func<CancellationToken,Task>? reconnect=null,Func<bool>? applied=null)
{
    private readonly Dictionary<(BattleFighterSide,long),(string Delivery,BattleCommandReceipt Receipt)> acknowledgements=[];
    private readonly HashSet<string> cuts=[];
    private bool running;
    private int callbacks;
    internal int PendingCallbacks=>Volatile.Read(ref callbacks);
    internal string[] PendingCuts=>cuts.ToArray();
    private async Task<T> Main<T>(Func<T> work)
    {Interlocked.Increment(ref callbacks);try{return await MainThread.Get(work);}finally{Interlocked.Decrement(ref callbacks);}}
    internal async Task<BattleCancellationWork?> Run(CancellationToken ct)
    {
        if(running)throw Invalid();running=true;
        await Main(()=>{RequireRuntime();controller.Acknowledged+=Acknowledged;controller.CutApplied+=CutApplied;return true;});
        long heartbeat=Stopwatch.GetTimestamp();
        long lastView=heartbeat;
        ByteString view=ByteString.Empty;
        try
        {
            while(!ct.IsCancellationRequested)
            {
                try
                {
                    var connection=connectionFor();
                    if(Stopwatch.GetElapsedTime(heartbeat)>=TimeSpan.FromSeconds(10)){await connection.Heartbeat(true,ct);heartbeat=Stopwatch.GetTimestamp();}
                    if(Stopwatch.GetElapsedTime(lastView)>=TimeSpan.FromMilliseconds(250))
                    {
                        var captured=await Main(()=>{RequireRuntime();return controller.CaptureView();});
                        if(!captured.IsEmpty)view=captured;
                        lastView=Stopwatch.GetTimestamp();
                    }
                    var lease=connection.Lease??throw Invalid();
                    var ack=await Main(()=>BuildAcknowledgement(lease));
                    if(ack is not null)
                    {
                        // A lost ACK reply can leave the server ready to dispatch the next delivery.
                        await connection.Api.AcknowledgeCommandsAsync(ack,connection.Options(ct));
                        await Main(() =>
                        {
                            RequireRuntime();
                            foreach (var receipt in ack.Receipts) acknowledgements.Remove((receipt.Side, receipt.Sequence));
                            foreach (string cut in ack.AppliedCutIds) cuts.Remove(cut);
                            return true;
                        });
                    }
                    var work=await connection.Api.PollRunningAsync(new(){Executor=lease,Scope=scope.Clone(),View=view},connection.Options(ct));
                    if (work.Aborted is not null) return work.Aborted;
                    view=ByteString.Empty;
                    await Main(()=>{RequireRuntime();controller.ApplyRunningWork(work);return true;});
                    await Task.Delay(50,ct);
                }
                catch(RpcException error)when(error.StatusCode is StatusCode.Unavailable or StatusCode.DeadlineExceeded or StatusCode.ResourceExhausted or StatusCode.FailedPrecondition)
                {
                    if(applied?.Invoke()==true)return null;
                    if (error.StatusCode == StatusCode.FailedPrecondition && await ReadAbortAsync(connectionFor(), ct) is { } abort)
                        return abort;
                    await Main(()=>{RequireRuntime();controller.TransportFailed();return true;});
                    if(reconnect is not null)await reconnect(ct);await Task.Delay(500,ct);
                }
            }
        }
        catch(OperationCanceledException)when(ct.IsCancellationRequested){}
        catch(RpcException error)when(ct.IsCancellationRequested && error.StatusCode==StatusCode.Cancelled){}
        catch(Exception error)
        {
            await Main(()=>{if(ReferenceEquals(BattleTickController.Current,controller))controller.Fail(error);return true;});throw;
        }
        finally
        {
            await Main(()=>{controller.Acknowledged-=Acknowledged;controller.CutApplied-=CutApplied;return true;});
        }
        return null;
    }
    internal static async Task<BattleCancellationWork?> ReadAbortAsync(BattleExecutorConnection connection, CancellationToken ct,
        bool tolerateTransient = true)
    {
        try
        {
            var reply = await connection.Poll(ct);
            return reply.Cancelled is { Terminal.Kind: BattleCleanupTerminalKind.StartedAbort } aborted ? aborted : null;
        }
        catch (RpcException error) when (error.StatusCode == StatusCode.FailedPrecondition
            || tolerateTransient && error.StatusCode is (StatusCode.Unavailable or StatusCode.DeadlineExceeded or StatusCode.ResourceExhausted))
        { return null; }
    }
    private void Acknowledged(BattleQueuedCommand command,BattleCommandReceipt receipt)
    {
        var key=(receipt.Side,receipt.Sequence);
        if(acknowledgements.TryGetValue(key,out var prior) && (prior.Delivery!=command.DeliveryId || !prior.Receipt.Equals(receipt)))throw Invalid();
        if(!acknowledgements.ContainsKey(key) && acknowledgements.Count>=8)throw Invalid();
        acknowledgements[key]=(command.DeliveryId,receipt.Clone());
    }
    private void CutApplied(string cut)
    {if(cuts.Count>=2 && !cuts.Contains(cut))throw Invalid();cuts.Add(cut);}
    private BattleExecutorCommandsAck? BuildAcknowledgement(BattleExecutorLeaseMessage lease)
    {
        RequireRuntime();if(acknowledgements.Count==0 && cuts.Count==0)return null;
        string delivery=acknowledgements.Values.FirstOrDefault().Delivery??"";
        if(acknowledgements.Values.Any(a=>a.Delivery!=delivery))throw Invalid();
        var ack=new BattleExecutorCommandsAck{Executor=lease,Scope=scope.Clone(),DeliveryId=delivery};
        ack.Receipts.Add(acknowledgements.Values.Select(a=>a.Receipt.Clone()));ack.AppliedCutIds.Add(cuts);return ack;
    }
    private void RequireRuntime()
    {if(!ReferenceEquals(BattleTickController.Current,controller) || controller.Failure is not null || !scope.Equals(BattleGameContext.Scope))throw Invalid();}
    private static InvalidDataException Invalid()=>new("battle-executor-running-pump-invalid");
}
