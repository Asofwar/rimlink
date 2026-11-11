using System.Diagnostics;
using System.Security.Cryptography;
using Google.Protobuf;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;

namespace Rimlink.Client.GuildCapture;

public static class BattleArtifactDelivery
{
    public static async Task Upload(BattleExecutorConnection connection,BattleScopeMessage scope,string kind,BattleDocumentDescriptor descriptor,string path,CancellationToken ct)
    {
        var expected=kind switch{"initial"=>BattleDocumentKind.InitialSnapshot,"final"=>BattleDocumentKind.FinalSnapshot,"simulation"=>BattleDocumentKind.SimulationJournal,"view"=>BattleDocumentKind.TerminalView,_=>throw Invalid()};
        BattleManifestContracts.RequireDescriptor(descriptor,expected);BattleExecutorSettings.RequireNoLinks(path);
        using var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
        if(input.Length!=descriptor.SizeBytes || !SHA256.HashData(input).AsSpan().SequenceEqual(descriptor.Sha256.Span))throw Invalid();
        long offset=0,heartbeat=Stopwatch.GetTimestamp();
        while(offset<input.Length)
        {
            if(Stopwatch.GetElapsedTime(heartbeat)>=TimeSpan.FromSeconds(10)){await connection.Heartbeat(true,ct);heartbeat=Stopwatch.GetTimestamp();}
            byte[] bytes=new byte[(int)Math.Min(262144,input.Length-offset)];input.Position=offset;await input.ReadExactlyAsync(bytes,ct);
            var chunk=new BattleArtifactChunk{Executor=connection.Lease??throw Invalid(),Scope=scope.Clone(),TotalBytes=descriptor.SizeBytes,Sha256=descriptor.Sha256,Offset=offset,Data=ByteString.CopyFrom(bytes)};
            var progress=kind=="initial"?await connection.Api.UploadInitialAsync(chunk,connection.Options(ct)):await connection.Api.UploadResultArtifactAsync(new(){Kind=kind,Chunk=chunk},connection.Options(ct));
            if(progress.ReceivedBytes<offset+bytes.Length || progress.ReceivedBytes>descriptor.SizeBytes || progress.Complete!=(progress.ReceivedBytes==descriptor.SizeBytes))throw Invalid();
            if(progress.Complete && (progress.Artifact is null || !progress.Artifact.Scope.Equals(scope) || progress.Artifact.Kind!=kind
                || progress.Artifact.SizeBytes!=descriptor.SizeBytes || !progress.Artifact.Sha256.Equals(descriptor.Sha256)))throw Invalid();
            offset=progress.ReceivedBytes;
        }
    }
    private static InvalidDataException Invalid()=>new("battle-artifact-delivery-invalid");
}
