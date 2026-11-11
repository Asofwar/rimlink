using Google.Protobuf;
using Rimlink.Protocol.V1;

namespace Rimlink.Protocol;

public static class BattleRuntimeStartedContracts
{
    public static BattleRuntimeStartedProofV1 Read(ReadOnlySpan<byte> bytes)
    {
        if(bytes.Length is <1 or >2048)throw Invalid();BattleRuntimeStartedProofV1 value;
        try{value=BattleRuntimeStartedProofV1.Parser.WithDiscardUnknownFields(true).ParseFrom(bytes.ToArray());}catch(InvalidProtocolBufferException){throw Invalid();}
        if(!value.ToByteArray().AsSpan().SequenceEqual(bytes) || value.Version!=1 || value.Scope is not{} scope || string.IsNullOrWhiteSpace(scope.WorldInstanceId)
            || scope.WorldInstanceId.Length>256 || scope.WorldInstanceId.Any(char.IsControl) || !Uuid(scope.BattleId) || !Uuid(scope.RestoreBoundary) || scope.Attempt<=0 || scope.Fence<=0
            || !Uuid(value.RuntimeInstanceId) || !Uuid(value.AssignmentTransitionId) || !Uuid(value.StartRequestId) || value.StartDigest.Length!=32
            || !value.GameLoaded || !value.TickRuntimeBound || value.InitialTick<0 || value.InitialTick>int.MaxValue-30000
            || value.ReceiptJournalSha256.Length!=32 || value.ReceiptJournalBytes is <40 or >67108864)throw Invalid();
        BattleManifestContracts.RequireDescriptor(value.InitialSnapshot,BattleDocumentKind.InitialSnapshot);return value;
    }
    private static bool Uuid(string value)=>Guid.TryParseExact(value,"D",out var id) && id!=Guid.Empty && id.ToString("D")==value;
    private static InvalidDataException Invalid()=>new("battle-runtime-started-proof-invalid");
}
