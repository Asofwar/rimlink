using System.Security.Cryptography;
using Google.Protobuf;
using Rimlink.Client.GuildCapture;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Xunit;

namespace Rimlink.Client.Tests.GuildCapture;

public sealed class BattlePreparedViewDownloadTests
{
    [Theory]
    [InlineData("valid")][InlineData("offset")][InlineData("digest")][InlineData("empty")][InlineData("complete")][InlineData("bytes")][InlineData("oversize")][InlineData("side")][InlineData("started-defender")]
    public async Task Initial_download_checks_every_page_and_only_publishes_the_complete_raw_hash(string fault)
    {
        string directory=Path.Combine(Path.GetTempPath(),"battle-initial-download-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        try
        {
            var manifest=BattleManifestContractsTests.Manifest();byte[] raw=Enumerable.Range(0,300000).Select(n=>(byte)n).ToArray();
            bool startedDefender = fault == "started-defender";
            if (startedDefender) manifest.Defender = new() { DefenderAi = true };
            manifest.InitialSnapshot=new(){Kind=BattleDocumentKind.InitialSnapshot,SizeBytes=raw.Length,Sha256=ByteString.CopyFrom(SHA256.HashData(raw))};
            var request=new BattlePreparedViewRequest{Scope=new(){WorldInstanceId=manifest.WorldInstanceId,RestoreBoundary=manifest.RestoreBoundary,BattleId=manifest.BattleId,Attempt=1,Fence=1},
                HomeVersion=3,Home=new(){Scope=new(){WorldInstanceId=manifest.WorldInstanceId,RestoreBoundary=manifest.RestoreBoundary,PlayerId=startedDefender ? 1 : 2},SnapshotId=Guid.NewGuid().ToString("N"),RawSha256=ByteString.CopyFrom(new byte[32])},ProfileDigest=manifest.ProfileDigest};
            var view=new BattlePreparedViewReply{Manifest=manifest.ToByteString(),ManifestDigest=ByteString.CopyFrom(BattleManifestContracts.Digest(BattleDocumentKind.PreparedManifest,manifest.ToByteArray())),InitialSnapshot=manifest.InitialSnapshot.Clone(),Side=fault=="side"||startedDefender?BattleFighterSide.Defender:BattleFighterSide.Attacker,StartCommitted=startedDefender};
            int calls=0;string path=Path.Combine(directory,"initial.rws");
            Task<BattlePreparedInitialReply> Page(BattlePreparedInitialRequest page,CancellationToken token)
            {
                calls++;int length=(int)Math.Min(262144,raw.Length-page.Offset);byte[] bytes=raw.AsSpan((int)page.Offset,length).ToArray();
                if(fault=="bytes")bytes[0]^=1;
                return Task.FromResult(new BattlePreparedInitialReply{Offset=fault=="offset"?page.Offset+1:page.Offset,ManifestDigest=fault=="digest"?ByteString.CopyFrom(new byte[32]):view.ManifestDigest,
                    Data=fault=="empty"?ByteString.Empty:fault=="oversize"?ByteString.CopyFrom(new byte[262145]):ByteString.CopyFrom(bytes),Complete=fault=="complete"||page.Offset+length==raw.Length});
            }
            if(fault=="valid" || startedDefender)
            {
                await BattlePreparedViewDownload.ReadAsync(request,view,path,Page,default);Assert.Equal(raw,File.ReadAllBytes(path));Assert.Equal(2,calls);
                await BattlePreparedViewDownload.ReadAsync(request,view,path,Page,default);Assert.Equal(2,calls);
            }
            else{await Assert.ThrowsAsync<InvalidDataException>(()=>BattlePreparedViewDownload.ReadAsync(request,view,path,Page,default));Assert.False(File.Exists(path));}
        }
        finally{Directory.Delete(directory,true);}
    }
}
