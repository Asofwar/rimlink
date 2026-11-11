using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using Rimlink.Client.GuildCapture;
using Rimlink.Protocol.V1;
using Xunit;

namespace Rimlink.Client.Tests.GuildCapture;

public sealed class BattleProfileCaptureTests
{
    [Theory]
    [InlineData(null,"unknown")][InlineData("","unknown")][InlineData(" ","unknown")][InlineData("1.7.5000","1.7.5000")]
    public void A_missing_official_mod_version_is_explicitly_unknown(string? actual,string expected)
    {Assert.Equal(expected,BattleRuntimeFacts.NormalizeVersion(actual));}
    [Theory]
    [InlineData("valid")][InlineData("large-inventory")][InlineData("extra-file")][InlineData("outside-build")][InlineData("duplicate-root")][InlineData("personal-template")]
    public void Capture_closes_actual_files_and_build_locations_before_a_profile_can_be_loaded(string fault)
    {
        string directory=Path.Combine(Path.GetTempPath(),"battle-profile-capture-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        try
        {
            string game=Path.Combine(directory,"game"),runtime=Path.Combine(directory,"runtime"),mod=Path.Combine(directory,"mod");
            foreach(string path in new[]{game,runtime,mod})Directory.CreateDirectory(path);
            string gameBuild=Path.Combine(game,"game.dll"),runtimeBuild=Path.Combine(runtime,"runtime.dll"),core=Path.Combine(mod,"core.dll"),client=Path.Combine(mod,"client.dll");
            foreach(string path in new[]{gameBuild,runtimeBuild,core,client})File.WriteAllText(path,"unit bytes "+Path.GetFileName(path),Encoding.UTF8);
            if(fault=="large-inventory")for(int i=0;i<400;i++)File.WriteAllText(Path.Combine(game,"runtime-component-"+i.ToString("D4")+".bin"),"x",Encoding.UTF8);
            string outside=Path.Combine(directory,"outside.dll");if(fault=="outside-build"){File.Copy(client,outside);client=outside;}
            var roots=new[]{new BattleRuntimeRootPath(BattleInventoryRoot.Game,"game",game),new(BattleInventoryRoot.Runtime,"runtime",runtime),new(BattleInventoryRoot.Mod,"mod",mod)};
            if(fault=="duplicate-root")roots=[..roots,roots[0]];
            var facts=new BattleRuntimeFacts([new(){LoadIndex=0,PackageId="test.mod",Version="1",RootId="mod"}],[],"unit config"u8.ToArray(),"unit defs"u8.ToArray(),gameBuild,runtimeBuild,core,client);
            string template=Path.Combine(directory,"template.rws");
            string personal=fault=="personal-template"?$"<components><li Class='Rimlink.Client.Game.RimlinkGameComponent'><rimlinkRecoveryVersion>1</rimlinkRecoveryVersion><rimlinkSnapshotId>{Guid.NewGuid():N}</rimlinkSnapshotId></li></components>":"";
            File.WriteAllText(template,$"<savegame><game>{personal}<maps><li><mapInfo><size>(250, 1, 250)</size></mapInfo></li></maps></game></savegame>",new UTF8Encoding(false));
            string archive=Path.Combine(directory,"archive"),profileFile=Path.Combine(directory,"profile.pb");
            if(fault is "outside-build" or "duplicate-root" or "personal-template")
            {Assert.Throws<InvalidDataException>(()=>BattleProfileCapture.Create(facts,roots,template,archive,profileFile));return;}
            var captured=BattleProfileCapture.Create(facts,roots,template,archive,profileFile);
            Assert.Equal(8,captured.Profile.Fighters.Count);Assert.Equal(3000,captured.Profile.Rules.CaptureTicks);
            Assert.All(captured.Profile.Fighters,f=>Assert.Equal(1u,f.BaselinerAppearanceVersion));
            if(fault=="extra-file")File.WriteAllText(Path.Combine(game,"unexpected.dll"),"drift",Encoding.UTF8);
            if(fault is "valid" or "large-inventory"){captured.VerifyRuntime(roots,facts);captured.VerifyViewerRuntime(roots,facts);}
            else Assert.Throws<InvalidDataException>(()=>captured.VerifyRuntime(roots,facts));
            if(fault=="large-inventory"){Assert.True(captured.Profile.Inventory.SizeBytes>16384);Assert.True(captured.Profile.ViewerCompatibility.SizeBytes<1024);}
            Assert.Equal(SHA256.HashData(File.ReadAllBytes(client)),captured.Profile.ViewerBuildSha256.ToByteArray());
            if (fault is "valid" or "large-inventory")
            {
                var document = captured.Profile.ViewerCompatibility;
                var compatibility = File.ReadAllBytes(Path.Combine(archive, "guild-profiles", ((int)document.Kind).ToString(),
                    Convert.ToHexStringLower(document.Sha256.Span) + ".artifact"));
                var entry = new BattlePreparedEntry
                {
                    Profile = ByteString.CopyFrom(File.ReadAllBytes(profileFile)),
                    ProfileDigest = captured.Digest,
                    ViewerCompatibility = ByteString.CopyFrom(compatibility),
                };
                var inventory = captured.Inventory.ToByteArray();
                BattleExecutorProfile.LoadViewer(entry, inventory).VerifyViewerRuntime(roots, facts);
                inventory[^1] ^= 1;
                Assert.Throws<InvalidDataException>(() => BattleExecutorProfile.LoadViewer(entry, inventory));
                inventory[^1] ^= 1;
                compatibility[^1] ^= 1;
                entry.ViewerCompatibility = ByteString.CopyFrom(compatibility);
                Assert.Throws<InvalidDataException>(() => BattleExecutorProfile.LoadViewer(entry, inventory));
            }
        }
        finally{Directory.Delete(directory,true);}
    }
}
