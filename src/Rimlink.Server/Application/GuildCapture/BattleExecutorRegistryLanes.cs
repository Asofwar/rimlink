using Google.Protobuf;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;

namespace Rimlink.Server.Application.GuildCapture;

internal sealed partial class BattleExecutorRegistry
{
    private sealed record Courier(BattleExecutorLease Lease,BattleExecutorHelloBindingV1 Binding,string Boot,long Observed,GuildOwnedProcess? Process);
    private readonly Dictionary<string,Courier> couriers=[];
    private BattleExecutorHelloBindingV1? executionBinding;
    private GuildOwnedProcess? ownedProcess;
    private string executionBoot="";
    private bool startReplayOnly;

    internal BattleExecutorLease RegisterExecutionAuthenticated(BattleExecutorHelloBindingV1 binding,string boot,GuildOwnedProcess? process = null)
    {
        binding=BattleExecutorHelloContracts.Read(binding.ToByteArray());RequireBoot(boot);
        lock(gate)
        {
            if(binding.Lane!=BattleExecutorLane.Execution || binding.ExecutorId!=executorId || !profile.IsEmpty && !binding.ProfileDigest.Equals(profile))throw Refused();
            bool same=executionBinding?.RuntimeInstanceId==binding.RuntimeInstanceId && executionBoot==boot;
            if((Fresh() && executionBinding is not null || busy || cleanupHeld) && !same)throw Refused();
            if(same && (!executionBinding!.ProfileDigest.Equals(binding.ProfileDigest) || !executionBinding.InventorySha256.Equals(binding.InventorySha256)
                || !executionBinding.ExecutorBuildSha256.Equals(binding.ExecutorBuildSha256) || ownedProcess != process))throw Refused();
            current=new(executorId,Guid.NewGuid().ToString("D"),checked(++fence),binding.ProfileDigest);
            executionBinding=binding.Clone();ownedProcess=process;startReplayOnly=binding.RetainedContext?.Phase==BattleExecutorRetainedPhase.StartAcknowledgementPending;executionBoot=boot;observed=time.GetTimestamp();if(!same)busy=false;
            return current;
        }
    }
    internal GuildOwnedProcess? OwnedProcessOf(BattleExecutorLease lease)
    { lock (gate) { RequireOccupant(lease); return ownedProcess; } }
    internal BattleExecutorLease RegisterRecoveryAuthenticated(BattleExecutorHelloBindingV1 binding,string boot,GuildOwnedProcess? process = null)
    {
        binding=BattleExecutorHelloContracts.Read(binding.ToByteArray());RequireBoot(boot);
        lock(gate)
        {
            if(binding.Lane!=BattleExecutorLane.RecoveryDelivery || binding.ExecutorId!=executorId || binding.BattleId==""
                || binding.ReplaceRestoredExecutor && process is null)throw Refused();
            foreach(string id in couriers.Where(c=>!FreshAt(c.Value.Observed)).Select(c=>c.Key).ToArray())couriers.Remove(id);
            if(couriers.Count>=8)throw Refused();
            var lease=new BattleExecutorLease(executorId,Guid.NewGuid().ToString("D"),checked(++fence),binding.ProfileDigest);
            couriers.Add(lease.LeaseId,new(lease,binding.Clone(),boot,time.GetTimestamp(),process));return lease;
        }
    }
    internal GuildOwnedProcess ReplacementProcess(BattleExecutorLease lease)
    {
        lock(gate)
        {
            Require(lease);
            return couriers.TryGetValue(lease.LeaseId,out var courier) && courier.Binding.ReplaceRestoredExecutor
                ? courier.Process ?? throw Refused() : throw Refused();
        }
    }
    internal BattleExecutorHelloBindingV1? BindingOf(BattleExecutorLease lease)
    {
        lock(gate){Require(lease);return couriers.TryGetValue(lease.LeaseId,out var courier)?courier.Binding.Clone():executionBinding?.Clone();}
    }
    internal BattleExecutorLeaseMessage Wire(BattleExecutorLease lease)
    {
        lock(gate)
        {
            Require(lease);bool delivery=couriers.TryGetValue(lease.LeaseId,out var courier);
            return new(){ExecutorId=lease.ExecutorId,LeaseId=lease.LeaseId,Fence=lease.Fence,ProfileDigest=lease.ProfileDigest,
                Binding=delivery?courier!.Binding.Clone():executionBinding?.Clone(),ServerBootId=delivery?courier!.Boot:executionBoot,ValidForMs=30000,StartReplayOnly=!delivery && startReplayOnly};
        }
    }
    internal void RequireExecution(BattleExecutorLease lease)
    {lock(gate){RequireOccupant(lease);if(executionBinding?.Lane!=BattleExecutorLane.Execution || startReplayOnly)throw Refused();}}
    internal void RequireExecutionTransport(BattleExecutorLease lease)
    {lock(gate){RequireOccupant(lease);if(executionBinding?.Lane!=BattleExecutorLane.Execution)throw Refused();}}
    internal void ConfirmRuntimeStarted(BattleExecutorLease lease,BattleRuntimeStartedProofV1 proof)
    {
        lock(gate)
        {
            RequireExecutionTransport(lease);if(executionBinding!.RuntimeInstanceId!=proof.RuntimeInstanceId)throw Refused();
            if(startReplayOnly && (executionBinding.BattleId!=proof.Scope.BattleId || !executionBinding.StartDigest.Equals(proof.StartDigest)
                || executionBinding.RetainedContext!.StartRequestId!=proof.StartRequestId))throw Refused();
            startReplayOnly=false;
        }
    }
    private void RequireOccupant(BattleExecutorLease lease)
    {Require(lease);if(current!=lease)throw Refused();}
    private bool FreshAt(long at)=>time.GetElapsedTime(at,time.GetTimestamp()) is var elapsed && elapsed>=TimeSpan.Zero && elapsed<=TimeSpan.FromSeconds(30);
    private static void RequireBoot(string boot)
    {if(!Guid.TryParseExact(boot,"D",out var id) || id==Guid.Empty || id.ToString("D")!=boot)throw Refused();}
}
