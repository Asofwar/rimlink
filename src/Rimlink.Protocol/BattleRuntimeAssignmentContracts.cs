using Google.Protobuf;
using Rimlink.Protocol.V1;

namespace Rimlink.Protocol;

public static class BattleRuntimeAssignmentContracts
{
    public static BattleRuntimeAssignmentV1 Read(ReadOnlySpan<byte> bytes)
    {
        if(bytes.Length is <1 or >2048)throw Invalid();BattleRuntimeAssignmentV1 value;
        try{value=BattleRuntimeAssignmentV1.Parser.WithDiscardUnknownFields(true).ParseFrom(bytes.ToArray());}catch(InvalidProtocolBufferException){throw Invalid();}
        if(!value.ToByteArray().AsSpan().SequenceEqual(bytes) || value.Version!=1 || value.Scope is not{} scope || !Uuid(scope.BattleId) || !Uuid(scope.RestoreBoundary)
            || !Text(scope.WorldInstanceId,256) || scope.Attempt<=0 || scope.Fence<=0 || !Uuid(value.CampaignId) || !Uuid(value.RuntimeInstanceId)
            || !Text(value.ExecutorId,128) || !Uuid(value.OriginalLeaseId) || value.OriginalLeaseFence<=0 || value.StartNonce.Length!=32
            || !Uuid(value.ApprovalTransitionId) || value.ProfileDigest.Length!=32 || value.InventorySha256.Length!=32 || value.ExecutorBuildSha256.Length!=32
            || value.AssignedAtUtcTicks<=0 || value.AssignedAtUtcTicks>DateTime.MaxValue.Ticks || !Uuid(value.TransitionId))throw Invalid();
        return value;
    }
    private static bool Uuid(string value)=>Guid.TryParseExact(value,"D",out var id) && id!=Guid.Empty && id.ToString("D")==value;
    private static bool Text(string value,int maximum)=>!string.IsNullOrWhiteSpace(value) && value.Length<=maximum && !value.Any(char.IsControl);
    private static InvalidDataException Invalid()=>new("battle-runtime-assignment-invalid");
}
