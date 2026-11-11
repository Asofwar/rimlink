using System.Text;
using System.Text.Json;
using Google.Protobuf;
using Rimlink.Client.GuildCapture;
using Rimlink.Client.Net;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Saves;
using Rimlink.Server.Storage.Entities;
using Xunit;

namespace Rimlink.Client.Tests.GuildCapture;

public sealed class BattleCanonicalHomeTests
{
    [Theory]
    [InlineData("valid")][InlineData("missing-receipt")][InlineData("not-current")][InlineData("hash")][InlineData("snapshot")]
    [InlineData("epoch")][InlineData("version")][InlineData("scope")][InlineData("battle")][InlineData("mutated-file")]
    [InlineData("endpoint")][InlineData("new-version")][InlineData("boundary")]
    public void Handoff_requires_an_accepted_exact_home_XML_and_never_uses_an_unacknowledged_local_save(string fault)
    {
        string directory=Path.Combine(Path.GetTempPath(),"battle-home-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        try
        {
            string snapshot=Guid.NewGuid().ToString("N"),path=Path.Combine(directory,"home.rws");
            var scope=new GuildColonyScope{WorldInstanceId="world",RestoreBoundary=Guid.NewGuid().ToString("D"),PlayerId=7,SaveGeneration=2,SaveWriteEpoch=3};
            var metadata=new OutpostSaveMetadata{SchemaVersion=1,Scope=new(){WorldInstanceId=scope.WorldInstanceId,RestoreBoundary=scope.RestoreBoundary,
                OwnerPlayerId=7,SaveGeneration=2,SaveWriteEpoch=3},GuildMetadata=new(){SchemaVersion=1,Scope=scope.Clone()}};
            string xml=$"<savegame><game><components><li Class='{SaveRecoveryData.ComponentClass}'><rimlinkRecoveryVersion>1</rimlinkRecoveryVersion><rimlinkSnapshotId>{snapshot}</rimlinkSnapshotId><rimlinkSaveGeneration>2</rimlinkSaveGeneration><rimlinkSaveWriteEpoch>3</rimlinkSaveWriteEpoch><rimlinkOutposts><version>1</version><metadata>{Convert.ToBase64String(metadata.ToByteArray())}</metadata></rimlinkOutposts>{(fault=="battle"?"<rimlinkBattleScope>foreign</rimlinkBattleScope>":"")}</li></components></game></savegame>";
            File.WriteAllText(path,xml,new UTF8Encoding(false));
            if(fault=="battle")
            {Assert.Throws<InvalidDataException>(()=>SaveMetaReader.Read(path));return;}
            // Use the shipping upload reply mapper; a hand-built acknowledgement hid missing proof fields.
            var saved=SaveMetaReader.Read(path);
            SaveInfo? accepted=SaveStore.ToInfo(new SaveVersion{Number=4,IsCurrent=true,SnapshotId=snapshot,SaveGeneration=2,
                SaveWriteEpoch=3,RecoveryVersion=1,RawSha256=saved.RawSha256,RecoveryJson=JsonSerializer.Serialize(saved.Recovery)});
            var retained=new BattleViewerMarkerV1{EndpointId="owned-stand",HomeVersion=4,
                Home=new(){Scope=scope.Clone(),SnapshotId=snapshot,RawSha256=ByteString.CopyFrom(saved.RawSha256!)}};
            switch(fault)
            {
                case "missing-receipt":accepted=null;break;case "not-current":accepted.IsCurrent=false;break;
                case "hash":accepted.RawSha256=ByteString.CopyFrom(new byte[32]);break;case "snapshot":accepted.SnapshotId=Guid.NewGuid().ToString("N");break;
                case "epoch":accepted.SaveWriteEpoch++;break;case "version":accepted.Version=0;break;case "scope":scope.SaveGeneration++;break;
                case "mutated-file":File.AppendAllText(path," ",Encoding.UTF8);break;
                case "new-version":accepted.Version++;break;
                case "boundary":accepted.OutpostRestoreBoundary=Guid.NewGuid().ToString("D");break;
            }
            if(fault=="valid")
            {
                var proof=BattleCanonicalHome.ReadAccepted(path,accepted,scope);Assert.Equal(snapshot,proof.SnapshotId);Assert.Equal(accepted!.RawSha256,proof.RawSha256);
                BattleCanonicalHome.RequireFile(path,proof);
                BattleCanonicalHome.RequireRetained(retained,"owned-stand",path,accepted,scope);
            }
            else Assert.Throws<InvalidDataException>(()=>BattleCanonicalHome.RequireRetained(retained,fault=="endpoint"?"other-stand":"owned-stand",path,accepted,scope));
        }
        finally{Directory.Delete(directory,true);}
    }
}
