using Google.Protobuf;
using Rimlink.Protocol.V1;

namespace Rimlink.Protocol;

public static class BattleCleanupContracts
{
    public static void ValidateTerminal(BattleCleanupTerminalBindingV1 value)
    {
        if(value is null || value.Version!=1 || !Uuid(value.TerminalTransitionId) || !Uuid(value.AssignmentTransitionId)
            || value.Kind is not(BattleCleanupTerminalKind.AppliedResult or BattleCleanupTerminalKind.PreStartCancellation or BattleCleanupTerminalKind.StartedAbort)
            || value.CommittedFinalDigest.Length != (value.Kind == BattleCleanupTerminalKind.AppliedResult ? 32 : 0)
            || value.AbortedStartDigest.Length != (value.Kind == BattleCleanupTerminalKind.StartedAbort ? 32 : 0))throw Invalid();
    }
    public static BattleCleanupProofV1 ReadProof(ReadOnlySpan<byte> bytes)
    {
        if(bytes.Length is <1 or >2048)throw Invalid();BattleCleanupProofV1 value;
        try{value=BattleCleanupProofV1.Parser.WithDiscardUnknownFields(true).ParseFrom(bytes.ToArray());}catch(InvalidProtocolBufferException){throw Invalid();}
        if(!value.ToByteArray().AsSpan().SequenceEqual(bytes))throw Invalid();ValidateTerminal(value.Terminal);ValidateScope(value.Scope);
        if(value.Version!=1 || !value.AtEntry || value.GameLoaded || value.BattleMapLoaded || value.TickRuntimeBound || value.HomeMarkerLoaded || value.PendingWorkCallbacks!=0
            || !value.AssignmentRejected || value.ProofKind is not (BattleCleanupProofKind.SameProcessEntry or BattleCleanupProofKind.ProvedReplacement) || !Uuid(value.ChallengeId) || value.Nonce.Length!=32
            || !value.CommittedFinalDigest.Equals(value.Terminal.CommittedFinalDigest) || value.InventorySha256.Length!=32 || !Uuid(value.RuntimeInstanceId))throw Invalid();
        return value;
    }
    public static void ValidateReceipt(BattleCleanupReceipt value)
    {
        if(value is null)throw Invalid();ValidateTerminal(value.Terminal);ValidateScope(value.Scope);
        if(value.Version!=1 || !Uuid(value.TransitionId) || !Uuid(value.RuntimeInstanceId)
            || value.ProofKind is not (BattleCleanupProofKind.SameProcessEntry or BattleCleanupProofKind.ProvedReplacement)
            || !value.CommittedFinalDigest.Equals(value.Terminal.CommittedFinalDigest) || value.CleanedAtUtcTicks<=0 || value.CleanedAtUtcTicks>DateTime.MaxValue.Ticks)throw Invalid();
    }
    private static void ValidateScope(BattleScopeMessage scope)
    {
        if(scope is null || string.IsNullOrWhiteSpace(scope.WorldInstanceId) || scope.WorldInstanceId.Length>256 || scope.WorldInstanceId.Any(char.IsControl)
            || !Uuid(scope.RestoreBoundary) || !Uuid(scope.BattleId) || scope.Attempt<=0 || scope.Fence<=0)throw Invalid();
    }
    private static bool Uuid(string value)=>Guid.TryParseExact(value,"D",out var id) && id!=Guid.Empty && id.ToString("D")==value;
    private static InvalidDataException Invalid()=>new("battle-cleanup-binding-invalid");
}
