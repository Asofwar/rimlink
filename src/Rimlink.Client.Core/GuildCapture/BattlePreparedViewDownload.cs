using System.Security.Cryptography;
using Google.Protobuf;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;

namespace Rimlink.Client.GuildCapture;

public static class BattlePreparedViewDownload
{
    public static BattlePreparedManifestV1 Validate(BattlePreparedViewRequest request,BattlePreparedViewReply reply)
    {
        if(request?.Scope is null || request.Home?.Scope is null || request.HomeVersion<=0 || request.Home.RawSha256.Length!=32
            || reply is null || reply.CalculateSize()>BattleManifestContracts.PreparedBytes+1024 || reply.ManifestDigest.Length!=32)throw Invalid();
        GuildResourceContracts.ValidateScope(request.Home.Scope);
        var manifest=BattleManifestContracts.ReadPrepared(reply.Manifest.Span);var scope=request.Scope;
        var side=reply.Side switch{BattleFighterSide.Attacker=>manifest.Attacker,BattleFighterSide.Defender=>manifest.Defender,_=>throw Invalid()};
        bool aiHandoff = reply.StartCommitted && reply.Side == BattleFighterSide.Defender && side.DefenderAi;
        if(manifest.WorldInstanceId!=scope.WorldInstanceId || manifest.RestoreBoundary!=scope.RestoreBoundary || manifest.BattleId!=scope.BattleId || manifest.Attempt!=scope.Attempt || manifest.Fence!=scope.Fence
            || !manifest.ProfileDigest.Equals(request.ProfileDigest) || !manifest.InitialSnapshot.Equals(reply.InitialSnapshot)
            || !BattleManifestContracts.Digest(BattleDocumentKind.PreparedManifest,reply.Manifest.Span).AsSpan().SequenceEqual(reply.ManifestDigest.Span)
            || !aiHandoff && (side.DefenderAi || side.PlayerId != request.Home.Scope.PlayerId) || request.Home.Scope.WorldInstanceId != scope.WorldInstanceId
            || request.Home.Scope.RestoreBoundary!=scope.RestoreBoundary)throw Invalid();
        return manifest;
    }
    public static async Task ReadAsync(BattlePreparedViewRequest request,BattlePreparedViewReply view,string path,
        Func<BattlePreparedInitialRequest,CancellationToken,Task<BattlePreparedInitialReply>> read,CancellationToken ct)
    {
        request=request.Clone();view=view.Clone();_=Validate(request,view);var descriptor=view.InitialSnapshot;
        path=Path.GetFullPath(path);BattleExecutorSettings.RequireNoLinks(path);BattleExecutorSettings.RequireNoLinks(path+".part");
        if(File.Exists(path)){RequireComplete(path,descriptor);return;}
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using(var file=new FileStream(path+".part",FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None))
        {
            long offset=file.Length;if(offset>descriptor.SizeBytes)throw Invalid();file.Position=offset;
            while(offset<descriptor.SizeBytes)
            {
                ct.ThrowIfCancellationRequested();
                var reply=await read(new(){View=request.Clone(),ManifestDigest=view.ManifestDigest,Offset=offset},ct);
                if(reply is null || reply.Offset!=offset || !reply.ManifestDigest.Equals(view.ManifestDigest)
                    || reply.Data.Length!=Math.Min(262144,descriptor.SizeBytes-offset) || reply.Complete!=(offset+reply.Data.Length==descriptor.SizeBytes))throw Invalid();
                file.Write(reply.Data.Span);file.Flush(true);offset+=reply.Data.Length;
            }
            file.Position=0;if(!SHA256.HashData(file).AsSpan().SequenceEqual(descriptor.Sha256.Span))throw Invalid();
        }
        ct.ThrowIfCancellationRequested();BattleExecutorSettings.RequireNoLinks(path);File.Move(path+".part",path,false);
    }
    private static void RequireComplete(string path,BattleDocumentDescriptor descriptor)
    {
        using var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
        if(file.Length!=descriptor.SizeBytes || !SHA256.HashData(file).AsSpan().SequenceEqual(descriptor.Sha256.Span))throw Invalid();
    }
    private static InvalidDataException Invalid()=>new("battle-prepared-view-initial-invalid");
}
