using System.Security.Cryptography;
using System.Xml.Linq;
using Google.Protobuf;
using Rimlink.Client.Economy;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;

namespace Rimlink.Client.GuildCapture;

// Operator preparation only: an inventory/profile file is evidence, never Product or native acceptance.
public static class BattleProfileCapture
{
    public static BattleExecutorProfile Create(BattleRuntimeFacts facts,IReadOnlyList<BattleRuntimeRootPath> roots,string templateFile,string archiveRoot,string profileFile)
    {
        if(roots.Count is <1 or >258 || roots.Select(r=>(r.Kind,r.Id)).Distinct().Count()!=roots.Count)throw Invalid();
        byte[] template=BattleExecutorSettings.ReadFile(templateFile,67108864);
        using(var input=new MemoryStream(template))
        {
            var recovery=SaveRecoveryData.ReadXml(input,out _);
            if(recovery.Version!=0 || recovery.SnapshotId!="" || recovery.Outposts.Present || recovery.ParcelIds.Length+recovery.Removed.Length+recovery.Returned.Length!=0)throw Invalid();
        }
        using(var input=new MemoryStream(template))
        {
            var document=XDocument.Load(input);
            if(document.Root?.Name.LocalName!="savegame" || document.Root.Element("game")?.Element("maps")?.Elements("li").Count()!=1
                || document.Descendants("mapInfo").Elements("size").SingleOrDefault()?.Value!="(250, 1, 250)")throw Invalid();
        }
        var inventory=new BattleRuntimeInventoryV1{Version=1,GameBuildSha256=Hash(facts.GameBuildPath),RuntimeBuildSha256=Hash(facts.RuntimeBuildPath),
            ClientBuildSha256=Hash(facts.ClientBuildPath),ExecutorBuildSha256=Hash(facts.ExecutorBuildPath)};
        inventory.Mods.AddRange(facts.Mods.Select(m=>m.Clone()));inventory.ActiveDlc.AddRange(facts.ActiveDlc.Order(StringComparer.Ordinal));
        int directories=0,files=0;var entries=new List<BattleFileInventoryEntry>();
        foreach(var root in roots)
        {
            if(root.Kind is not(BattleInventoryRoot.Game or BattleInventoryRoot.Runtime or BattleInventoryRoot.Mod))throw Invalid();
            string absolute=Path.GetFullPath(root.Path);BattleExecutorSettings.RequireNoLinks(absolute);
            var pending=new Stack<string>();pending.Push(absolute);
            while(pending.TryPop(out string? directory))
            {
                if(++directories>32768)throw Invalid();
                foreach(string path in Directory.EnumerateFileSystemEntries(directory))
                {
                    if((File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)throw Invalid();
                    if(Directory.Exists(path)){pending.Push(path);continue;}
                    if(++files>16384)throw Invalid();
                    using var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
                    entries.Add(new(){Root=root.Kind,RootId=root.Id,RelativePath=Path.GetRelativePath(absolute,path).Replace(Path.DirectorySeparatorChar,'/'),SizeBytes=input.Length,Sha256=ByteString.CopyFrom(SHA256.HashData(input))});
                }
            }
        }
        inventory.Files.AddRange(entries.OrderBy(e=>((int)e.Root)+"/"+e.RootId+"/"+e.RelativePath,StringComparer.Ordinal));
        inventory.Config=Put(BattleDocumentKind.Config,facts.Config);inventory.LoadedDefs=Put(BattleDocumentKind.Defs,facts.LoadedDefs);
        byte[] inventoryBytes=inventory.ToByteArray();_=BattleManifestContracts.ReadInventory(inventoryBytes);
        var inventoryDescriptor=Put(BattleDocumentKind.Inventory,inventoryBytes);
        var compatibility=new BattleViewerCompatibilityV1{Version=1,Inventory=inventoryDescriptor.Clone(),ViewerBuildSha256=inventory.ExecutorBuildSha256,SnapshotEncodingVersion=1,DeltaEncodingVersion=1};
        var profile=new BattleProfileV1{Version=1,ProfileId=Guid.NewGuid().ToString("D"),Revision=1,Inventory=inventoryDescriptor,
            ExecutorBuildSha256=inventory.ExecutorBuildSha256,ViewerBuildSha256=inventory.ExecutorBuildSha256,
            ViewerCompatibility=Put(BattleDocumentKind.ViewerCompatibility,compatibility.ToByteArray()),Template=Put(BattleDocumentKind.Template,template),
            SnapshotEncodingVersion=1,DeltaEncodingVersion=1,Rules=new(){CaptureTicks=3000,MaximumTicks=30000,WallSeconds=1200,AiAfterSeconds=30,TransportGraceSeconds=60},
            Limits=new(){CommandBytes=16384,CommandsPerSecondPerSide=20,PendingPerSide=64,SnapshotChunkBytes=262144,SnapshotBytes=67108864,FullDeltaFrameBytes=1048576},
            Arena=new(){Width=250,Height=250,ZoneMinX=122,ZoneMinZ=122,ZoneMaxX=127,ZoneMaxZ=127}};
        profile.CommandAllowlist.AddRange(["Move","Attack","Draft"]);
        foreach(var side in new[]{BattleFighterSide.Attacker,BattleFighterSide.Defender})for(int i=0;i<4;i++)
            profile.Fighters.Add(new BattleFighterSpec{FighterId=(side==BattleFighterSide.Attacker?"attacker-":"defender-")+(i+1),Side=side,PawnKindDef="Colonist",RaceDef="Human",AgeYears=25,Gender=BattleFighterGender.Male,
                X=side==BattleFighterSide.Attacker?108:141,Z=123+i,WeaponDef="Gun_BoltActionRifle",ApparelDefs={"Apparel_BasicShirt","Apparel_Pants"},Shooting=8,Melee=8,
                BaselinerAppearanceVersion=BattleManifestContracts.StandardBaselinerAppearanceVersion});
        profile.RosterDigest=ByteString.CopyFrom(BattleManifestContracts.RosterDigest(profile.Fighters));byte[] profileBytes=profile.ToByteArray();_=BattleManifestContracts.ReadProfile(profileBytes);
        Write(profileFile,profileBytes);
        var settings=new BattleExecutorSettings{ProfilePath=profileFile,ProfileSha256=Convert.ToHexStringLower(SHA256.HashData(profileBytes)),
            ProfileDigest=Convert.ToHexStringLower(BattleManifestContracts.Digest(BattleDocumentKind.Profile,profileBytes)),ArchiveRoot=archiveRoot};
        var measured=BattleExecutorProfile.Load(settings);measured.VerifyRuntime(roots,facts);return measured;

        BattleDocumentDescriptor Put(BattleDocumentKind kind,byte[] bytes)
        {
            var descriptor=new BattleDocumentDescriptor{Kind=kind,SizeBytes=bytes.Length,Sha256=ByteString.CopyFrom(SHA256.HashData(bytes))};
            BattleManifestContracts.RequireDescriptor(descriptor,kind);
            Write(Path.Combine(archiveRoot,"guild-profiles",((int)kind).ToString(System.Globalization.CultureInfo.InvariantCulture),Convert.ToHexStringLower(descriptor.Sha256.Span)+".artifact"),bytes);return descriptor;
        }
    }
    private static ByteString Hash(string path)
    {BattleExecutorSettings.RequireNoLinks(path);using var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);return ByteString.CopyFrom(SHA256.HashData(input));}
    private static void Write(string file,byte[] bytes)
    {
        BattleExecutorSettings.RequireNoLinks(file);
        if(File.Exists(file)){if(!File.ReadAllBytes(file).AsSpan().SequenceEqual(bytes))throw Invalid();return;}
        TransferOutbox.WriteDocument(file,bytes);
    }
    private static InvalidDataException Invalid()=>new("battle-profile-capture-invalid");
}
