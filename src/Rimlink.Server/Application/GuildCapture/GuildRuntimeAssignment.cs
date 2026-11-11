using Google.Protobuf;
using System.Text.Json.Serialization;
using Rimlink.Protocol.V1;

namespace Rimlink.Server.Application.GuildCapture;

internal sealed record GuildRuntimeAssignment(int Version,GuildMutationScope Scope,string BattleId,string CampaignId,long Attempt,long Fence,string ExecutorId,
    string RuntimeInstanceId,string OriginalLeaseId,long OriginalLeaseFence,string StartNonce,string ApprovalTransitionId,string ProfileDigest,
    string InventorySha256,string ExecutorBuildSha256,DateTime AssignedAtUtc,string TransitionId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] GuildOwnedProcess? OwnedProcess = null)
{
    internal BattleRuntimeAssignmentV1 Message()=>new(){Version=(uint)Version,Scope=new(){WorldInstanceId=Scope.WorldId,RestoreBoundary=Scope.Boundary,
        BattleId=BattleId,Attempt=Attempt,Fence=Fence},CampaignId=CampaignId,ExecutorId=ExecutorId,RuntimeInstanceId=RuntimeInstanceId,OriginalLeaseId=OriginalLeaseId,
        OriginalLeaseFence=OriginalLeaseFence,StartNonce=Bytes(StartNonce),ApprovalTransitionId=ApprovalTransitionId,ProfileDigest=Bytes(ProfileDigest),
        InventorySha256=Bytes(InventorySha256),ExecutorBuildSha256=Bytes(ExecutorBuildSha256),AssignedAtUtcTicks=AssignedAtUtc.Ticks,TransitionId=TransitionId};
    private static ByteString Bytes(string hex)=>ByteString.CopyFrom(Convert.FromHexString(hex));
}

internal sealed partial class BattleExecutorRegistry
{
    internal BattleExecutorLease RequireExecutionFor(BattlePreparedManifestV1 start)
    {
        lock(gate)
        {
            var lease=current??throw Refused();RequireExecution(lease);var binding=executionBinding!;
            if(binding.RuntimeInstanceId!=start.RuntimeInstanceId || binding.ExecutorId!=start.ExecutorId || binding.WorldInstanceId!=start.WorldInstanceId
                || binding.RestoreBoundary!=start.RestoreBoundary || !binding.ProfileDigest.Equals(start.ProfileDigest) || !binding.InventorySha256.Equals(start.MeasuredInventory.Sha256)
                || binding.BattleId!="" && (binding.BattleId!=start.BattleId || binding.Attempt!=start.Attempt || binding.Fence!=start.Fence))throw Refused();
            return lease;
        }
    }
    internal BattleExecutorLease RequireAssignedExecution(GuildRuntimeAssignment assignment,bool allowStartReplay=false)
    {
        lock(gate)
        {
            var lease=current??throw Refused();if(allowStartReplay)RequireExecutionTransport(lease);else RequireExecution(lease);var binding=executionBinding!;
            if(binding.RuntimeInstanceId!=assignment.RuntimeInstanceId || binding.ExecutorId!=assignment.ExecutorId || binding.WorldInstanceId!=assignment.Scope.WorldId
                || binding.RestoreBoundary!=assignment.Scope.Boundary || Convert.ToHexStringLower(binding.ProfileDigest.Span)!=assignment.ProfileDigest
                || Convert.ToHexStringLower(binding.InventorySha256.Span)!=assignment.InventorySha256 || Convert.ToHexStringLower(binding.ExecutorBuildSha256.Span)!=assignment.ExecutorBuildSha256
                || binding.BattleId!="" && (binding.BattleId!=assignment.BattleId || binding.Attempt!=assignment.Attempt || binding.Fence!=assignment.Fence
                    || binding.AssignmentTransitionId!=assignment.TransitionId))throw Refused();
            return lease;
        }
    }
}
