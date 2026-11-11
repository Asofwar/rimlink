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
    [InlineData("valid")][InlineData("large-inventory")][InlineData("extra-file")][InlineData("changed-build")][InlineData("outside-build")][InlineData("duplicate-root")][InlineData("personal-template")]
    public void Capture_pins_the_builds_and_their_locations_before_a_profile_can_be_loaded(string fault)
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
            if(fault=="changed-build")File.WriteAllText(gameBuild,"another build",Encoding.UTF8);
            if(fault is "valid" or "large-inventory" or "extra-file"){captured.VerifyRuntime(roots,facts);captured.VerifyViewerRuntime(facts);}
            else Assert.Throws<InvalidDataException>(()=>captured.VerifyRuntime(roots,facts));
            Assert.Equal(4,captured.Inventory.Files.Count);Assert.True(captured.Profile.ViewerCompatibility.SizeBytes<1024);
            if(fault!="changed-build")Assert.Equal(SHA256.HashData(File.ReadAllBytes(client)),captured.Profile.ViewerBuildSha256.ToByteArray());
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
                BattleExecutorProfile.LoadViewer(entry, inventory).VerifyViewerRuntime(facts);
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

    [Theory]
    [InlineData("steam-layout",true)][InlineData("own-settings",true)][InlineData("other-game-build",false)][InlineData("other-client-build",false)]
    [InlineData("other-mod-content",false)][InlineData("other-mod-version",false)][InlineData("extra-mod",false)][InlineData("other-dlc",false)]
    public void A_viewer_matches_by_builds_mods_and_definitions_wherever_its_files_live(string difference,bool accepted)
    {
        string directory=Path.Combine(Path.GetTempPath(),"battle-profile-viewer-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        try
        {
            // The arbiter: every mod inside its own copy of the game.
            string game=Path.Combine(directory,"arbiter","game"),mod=Path.Combine(game,"Mods","Rimlink");Directory.CreateDirectory(mod);
            string Put(string folder,string name,string text){Directory.CreateDirectory(folder);string path=Path.Combine(folder,name);File.WriteAllText(path,text,Encoding.UTF8);return path;}
            string gameBuild=Put(game,"game.dll","game 1"),runtimeBuild=Put(game,"runtime.dll","runtime 1"),core=Put(mod,"core.dll","core 1"),client=Put(mod,"client.dll","client 1");
            var roots=new[]{new BattleRuntimeRootPath(BattleInventoryRoot.Game,"game",game),new(BattleInventoryRoot.Runtime,"runtime",game),new(BattleInventoryRoot.Mod,"mod-0",mod)};
            BattleModInventoryEntry[] mods=[new(){LoadIndex=0,PackageId="asofwar.rimlink",Version="1",RootId="mod-0"}];
            var facts=new BattleRuntimeFacts(mods,["ludeon.rimworld.royalty"],"arbiter settings"u8.ToArray(),"packs 1"u8.ToArray(),gameBuild,runtimeBuild,core,client);
            string template=Put(directory,"template.rws","<savegame><game><maps><li><mapInfo><size>(250, 1, 250)</size></mapInfo></li></maps></game></savegame>");
            var captured=BattleProfileCapture.Create(facts,roots,template,Path.Combine(directory,"archive"),Path.Combine(directory,"profile.pb"));

            // The player: a Steam install with the mod in the Workshop folder, other files beside the game and personal mod settings.
            string steam=Path.Combine(directory,"steam","common","RimWorld"),workshop=Path.Combine(directory,"steam","workshop","content","294100","3830084263");
            Put(steam,"steam_appid.txt","294100");Put(Path.Combine(steam,"Mods","Other"),"About.xml","not loaded");
            var viewer=new BattleRuntimeFacts(
                difference=="extra-mod"?[..mods,new(){LoadIndex=1,PackageId="other.mod",Version="1",RootId="mod-1"}]
                    :difference=="other-mod-version"?[new(){LoadIndex=0,PackageId="asofwar.rimlink",Version="2",RootId="mod-0"}]:mods,
                difference=="other-dlc"?[]:["ludeon.rimworld.royalty"],
                difference=="own-settings"?"player settings"u8.ToArray():"arbiter settings"u8.ToArray(),
                difference=="other-mod-content"?"packs 2"u8.ToArray():"packs 1"u8.ToArray(),
                Put(steam,"game.dll",difference=="other-game-build"?"game 2":"game 1"),Put(steam,"runtime.dll","runtime 1"),
                Put(workshop,"core.dll","core 1"),Put(workshop,"client.dll",difference=="other-client-build"?"client 2":"client 1"));
            if(accepted)captured.VerifyViewerRuntime(viewer);
            else Assert.Throws<InvalidDataException>(()=>captured.VerifyViewerRuntime(viewer));
        }
        finally{Directory.Delete(directory,true);}
    }
}
