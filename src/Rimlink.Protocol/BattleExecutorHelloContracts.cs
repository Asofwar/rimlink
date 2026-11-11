using System.Security.Cryptography;
using Google.Protobuf;
using Rimlink.Protocol.V1;

namespace Rimlink.Protocol;

public static class BattleExecutorHelloContracts
{
    public static BattleExecutorHelloBindingV1 Read(ReadOnlySpan<byte> bytes)
    {
        if(bytes.Length is <1 or >2048)throw Invalid();BattleExecutorHelloBindingV1 value;
        try{value=BattleExecutorHelloBindingV1.Parser.WithDiscardUnknownFields(true).ParseFrom(bytes.ToArray());}catch(InvalidProtocolBufferException){throw Invalid();}
        if(!value.ToByteArray().AsSpan().SequenceEqual(bytes) || value.Version!=1 || !Text(value.ExecutorId,128) || !Uuid(value.RuntimeInstanceId)
            || value.ProfileDigest.Length!=32 || value.InventorySha256.Length!=32 || value.ExecutorBuildSha256.Length!=32
            || !Text(value.WorldInstanceId,256) || !Uuid(value.RestoreBoundary) || value.Lane is not(BattleExecutorLane.Execution or BattleExecutorLane.RecoveryDelivery))throw Invalid();
        GuildRuntimeContracts.Require(value.GuildRuntime);
        if(value.BattleId=="")
        {
            if(value.Lane!=BattleExecutorLane.Execution || value.Attempt!=0 || value.Fence!=0 || !value.StartDigest.IsEmpty || value.AssignmentTransitionId!="" || value.RetainedContext is not null || value.Entry is null
                || value.ReplaceRestoredExecutor)throw Invalid();
        }
        else if(!Uuid(value.BattleId) || value.Attempt<=0 || value.Fence<=0 || !Uuid(value.AssignmentTransitionId) || value.StartDigest.Length is not(0 or 32)
            || (value.ReplaceRestoredExecutor ? value.Entry is null || value.Lane != BattleExecutorLane.RecoveryDelivery || value.RetainedContext is not null
                : value.Entry is not null))throw Invalid();
        if(value.Entry is {} entry && (entry.Version!=1 || !entry.AtEntry || entry.GameLoaded || entry.PlayerSessionLoaded || entry.BattleContextBound || entry.TickRuntimeBound || entry.PendingWorkCallbacks!=0))throw Invalid();
        if(value.RetainedContext is {} context)
        {
            RequireContext(context);
            if(context.Phase==BattleExecutorRetainedPhase.StartAcknowledgementPending)
            {
                if(value.Lane!=BattleExecutorLane.Execution || value.StartDigest.Length!=32 || !Uuid(context.StartRequestId) || !context.GameLoaded || context.TickRuntimeBound
                    || context.TransportPosition!=0 || context.PendingCutIds.Count!=0 || context.PendingCallbacks!=0 || context.AssignmentRejected
                    || !context.ReceiptJournalSha256.IsEmpty || context.ReceiptJournalBytes!=0)throw Invalid();
                BattleManifestContracts.RequireDescriptor(context.InitialSnapshot,BattleDocumentKind.InitialSnapshot);
            }
            else if(context.StartRequestId!="" || context.InitialSnapshot is not null)throw Invalid();
        }
        return value;
    }
    public static byte[] Digest(BattleExecutorHelloBindingV1 value)
    {
        byte[] bytes=Read(value.ToByteArray()).ToByteArray();using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData("Rimlink.ExecutorHello\0V1"u8);hash.AppendData(bytes);return hash.GetHashAndReset();
    }
    private static void RequireContext(BattleExecutorRetainedContextV1 value)
    {
        if(value.Version!=1 || !Enum.IsDefined(value.Phase) || value.Phase==BattleExecutorRetainedPhase.Unspecified || value.StartNonce.Length!=32
            || value.CurrentTick<0 || value.TransportPosition<0 || value.PendingCallbacks is <0 or >64 || value.PendingCutIds.Count>2
            || value.PendingCutIds.Distinct().Count()!=value.PendingCutIds.Count || value.PendingCutIds.Any(id=>!Uuid(id))
            || value.ReceiptJournalSha256.Length is not(0 or 32) || value.ReceiptJournalSha256.IsEmpty && value.ReceiptJournalBytes!=0
            || !value.ReceiptJournalSha256.IsEmpty && value.ReceiptJournalBytes is <40 or >67108864)throw Invalid();
    }
    private static bool Uuid(string value)=>Guid.TryParseExact(value,"D",out var id) && id!=Guid.Empty && id.ToString("D")==value;
    private static bool Text(string value,int maximum)=>!string.IsNullOrWhiteSpace(value) && value.Length<=maximum && !value.Any(char.IsControl);
    private static InvalidDataException Invalid()=>new("battle-executor-hello-invalid");
}
