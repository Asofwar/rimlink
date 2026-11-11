using System.Text.Json;
using Google.Protobuf;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;

namespace Rimlink.Server.Application.GuildCapture;

internal sealed partial class BattleCommandJournal
{
    private BattleRuntimeStartedProofV1? runtimeStarted;
    internal BattleRuntimeStartedProofV1? RuntimeStarted {get{RequireHealthy();return runtimeStarted?.Clone();}}
    internal bool CanCompleteLostStart {get{RequireHealthy();return !sealedJournal && runtimeStarted is null && deliveries.Count==0 && cuts.Count==0;}}
    internal void BindRuntime(BattleRuntimeStartedProofV1 proof)
    {
        proof=BattleRuntimeStartedContracts.Read(proof.ToByteArray());RequireHealthy();
        if(runtimeStarted is not null){if(!runtimeStarted.Equals(proof))throw Invalid();return;}
        if(!CanCompleteLostStart)throw Invalid();RequireRuntimeScope(proof);
        using var buffer=new MemoryStream();using var writer=new BinaryWriter(buffer);
        writer.Write((byte)9);byte[] body=proof.ToByteArray();writer.Write(body.Length);writer.Write(body);byte[] frame=buffer.ToArray();AppendFrame(frame);AcceptFrame(frame);
    }
    private void AcceptRuntimeStarted(BinaryReader reader)
    {
        if(!CanCompleteLostStart)throw Invalid();var proof=BattleRuntimeStartedContracts.Read(ReadBody(reader));RequireRuntimeScope(proof);
        if(reader.BaseStream.Position!=reader.BaseStream.Length)throw Invalid();runtimeStarted=proof;
    }
    private void RequireRuntimeScope(BattleRuntimeStartedProofV1 proof)
    {
        var scope=JsonSerializer.Deserialize<BattleJournalScope>(scopeBytes,Json)??throw Invalid();
        if(proof.Scope.BattleId!=scope.BattleId || proof.Scope.WorldInstanceId!=scope.WorldId || proof.Scope.RestoreBoundary!=scope.Boundary
            || proof.Scope.Fence!=scope.Fence || !proof.StartDigest.Span.SequenceEqual(scope.StartDigest))throw Invalid();
    }
}
