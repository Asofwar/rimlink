using System.Security.Cryptography;
using System.Text.Json;
using Google.Protobuf;
using Rimlink.Client.GuildCapture;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Xunit;

namespace Rimlink.Client.Tests.GuildCapture;

public sealed class BattleRuntimeInventoryTests : IDisposable
{
    private readonly string directory=Path.Combine(Path.GetTempPath(),"rimlink-inventory-"+Guid.NewGuid().ToString("N"));
    private readonly BattleExecutorSettings settings;
    private readonly BattleRuntimeFacts facts;
    private readonly BattleProfileV1 profile;
    private string Game => Path.Combine(directory,"game");
    private static readonly byte[] Binary="fixture binary"u8.ToArray();
    public BattleRuntimeInventoryTests()
    {
        Directory.CreateDirectory(Game);Directory.CreateDirectory(Path.Combine(directory,"mod"));Directory.CreateDirectory(Path.Combine(directory,"runtime"));
        profile=BattleManifestContractsTests.Profile(); var inventory=BattleManifestContractsTests.Inventory(); inventory.Files.Clear();
        var paths=new[]{Path.Combine(Game,"Assembly-CSharp.dll"),Path.Combine(directory,"runtime","System.Private.CoreLib.dll"),Path.Combine(directory,"mod","Rimlink.Client.Core.dll"),Path.Combine(directory,"mod","Rimlink.Client.dll")};
        foreach(string path in paths) File.WriteAllBytes(path,Binary);
        inventory.GameBuildSha256=inventory.RuntimeBuildSha256=inventory.ClientBuildSha256=inventory.ExecutorBuildSha256=ByteString.CopyFrom(SHA256.HashData(Binary));
        inventory.Config=Put(BattleDocumentKind.Config,"settings"u8.ToArray());inventory.LoadedDefs=Put(BattleDocumentKind.Defs,"loaded defs"u8.ToArray());
        foreach(var entry in new[]{(BattleInventoryRoot.Game,"game",paths[0]),(BattleInventoryRoot.Runtime,"runtime",paths[1]),(BattleInventoryRoot.Mod,"rimlink",paths[2]),(BattleInventoryRoot.Mod,"rimlink",paths[3])})
            inventory.Files.Add(new BattleFileInventoryEntry{Root=entry.Item1,RootId=entry.Item2,RelativePath=Path.GetFileName(entry.Item3),SizeBytes=Binary.Length,Sha256=inventory.GameBuildSha256});
        Assert.Equal(inventory,BattleManifestContracts.ReadInventory(inventory.ToByteArray()));
        profile.ExecutorBuildSha256=inventory.ExecutorBuildSha256;profile.Inventory=Put(BattleDocumentKind.Inventory,inventory.ToByteArray());
        profile.Template=Put(BattleDocumentKind.Template,"fixture template"u8.ToArray());profile.ViewerCompatibility=Put(BattleDocumentKind.ViewerCompatibility,"fixture compatibility"u8.ToArray());
        profile.ExcludedRuntimePaths.Add(new BattleExcludedRuntimePath{Root=BattleInventoryRoot.Game,RootId="game",RelativePath="Logs/Player.log"});
        string profilePath=Path.Combine(directory,"profile.artifact");byte[] bytes=profile.ToByteArray();File.WriteAllBytes(profilePath,bytes);
        settings=new(){Version=1,ProfilePath=profilePath,ProfileSha256=Convert.ToHexStringLower(SHA256.HashData(bytes)),
            ProfileDigest=Convert.ToHexStringLower(BattleManifestContracts.Digest(BattleDocumentKind.Profile,bytes)),ArchiveRoot=directory,
            Roots=[new(BattleInventoryRoot.Game,"game",Game),new(BattleInventoryRoot.Runtime,"runtime",Path.Combine(directory,"runtime")),new(BattleInventoryRoot.Mod,"rimlink",Path.Combine(directory,"mod"))]};
        facts=new(inventory.Mods.Select(m=>m.Clone()).ToArray(),[],"settings"u8.ToArray(),"loaded defs"u8.ToArray(),paths[0],paths[1],paths[2],paths[3]);
    }
    private BattleDocumentDescriptor Put(BattleDocumentKind kind,byte[] bytes)
    {
        string hash=Convert.ToHexStringLower(SHA256.HashData(bytes));string path=Path.Combine(directory,"guild-profiles",((int)kind).ToString(),hash+".artifact");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);File.WriteAllBytes(path,bytes);
        return new(){Kind=kind,Sha256=ByteString.CopyFrom(SHA256.HashData(bytes)),SizeBytes=bytes.Length};
    }
    public void Dispose()=>Directory.Delete(directory,true);
    [Fact]
    public void Full_profile_closure_and_actual_loaded_builds_are_verified_without_writing_the_runtime()
    {
        Directory.CreateDirectory(Path.Combine(Game,"Logs"));File.WriteAllText(Path.Combine(Game,"Logs","Player.log"),"runtime log");
        var bundle=BattleExecutorProfile.Load(settings);bundle.VerifyRuntime(settings.Roots,facts);
        Assert.Equal(profile,bundle.Profile);Assert.Equal("runtime log",File.ReadAllText(Path.Combine(Game,"Logs","Player.log")));
    }
    [Theory]
    [InlineData("changed")][InlineData("missing")]
    [InlineData("mods")][InlineData("dlc")][InlineData("config")][InlineData("defs")][InlineData("loaded-build-path")]
    public void A_digest_alone_cannot_approve_a_different_actual_runtime(string damage)
    {
        var actual=facts;
        switch(damage){
            case "changed":File.WriteAllText(Path.Combine(Game,"Assembly-CSharp.dll"),"changed");break;
            case "missing":File.Delete(Path.Combine(Game,"Assembly-CSharp.dll"));break;
            case "mods":actual=facts with{Mods=[]};break;
            case "dlc":actual=facts with{ActiveDlc=["expansion"]};break;
            case "config":actual=facts with{Config="different"u8.ToArray()};break;
            case "defs":actual=facts with{LoadedDefs="different"u8.ToArray()};break;
            case "loaded-build-path":string other=Path.Combine(directory,"other.dll");File.WriteAllBytes(other,Binary);actual=facts with{ExecutorBuildPath=other};break;
        }
        Assert.Throws<InvalidDataException>(()=>BattleExecutorProfile.Load(settings).VerifyRuntime(settings.Roots,actual));
    }
    [Fact]
    public void Files_the_profile_does_not_pin_are_not_its_business()
    {
        // A log, a store file or another mod's folder beside the game must not stop an arbiter whose pinned builds are unchanged.
        File.WriteAllText(Path.Combine(Game,"extra.dll"),"extra");
        Directory.CreateDirectory(Path.Combine(Game,"Logs"));File.WriteAllText(Path.Combine(Game,"Logs","code.dll"),"code");
        BattleExecutorProfile.Load(settings).VerifyRuntime(settings.Roots,facts);
    }
    [Fact]
    public void Missing_or_tampered_reference_and_raw_vs_semantic_hash_confusion_are_refused()
    {
        string path=Path.Combine(directory,"guild-profiles",((int)BattleDocumentKind.Template).ToString(),Convert.ToHexStringLower(profile.Template.Sha256.Span)+".artifact");
        File.WriteAllText(path,"changed template");Assert.Throws<InvalidDataException>(()=>BattleExecutorProfile.Load(settings));
        File.WriteAllBytes(path,"fixture template"u8.ToArray());
        byte[] bytes=File.ReadAllBytes(settings.ProfilePath);bytes[0]^=1;File.WriteAllBytes(settings.ProfilePath,bytes);
        Assert.Throws<InvalidDataException>(()=>BattleExecutorProfile.Load(settings));
    }
}
