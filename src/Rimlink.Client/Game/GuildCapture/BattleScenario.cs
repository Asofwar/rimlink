using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;
using Google.Protobuf;
using RimWorld;
using Rimlink.Client.GuildCapture;
using Rimlink.Client.Game.Live;
using Rimlink.Client.Game.Raids;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using UnityEngine;
using Verse;
using Verse.AI.Group;

namespace Rimlink.Client.Game.GuildCapture;

// Disposable operator template only. No player session, custody, parcel or personal save path enters this adapter.
public sealed class BattleScenario
{
    private static readonly string[] StandardGenes=["Hair_DarkBlack","Skin_Melanin6"];
    private static readonly Color StandardHair=new(0.2f,0.2f,0.2f,1f);
    private static readonly Color StandardSkin=new(242f/255f,199f/255f,140f/255f,1f);
    private readonly BattleProfileV1 profile;
    private readonly BattleScopeMessage scope;
    public Map Map { get; private set; } = null!;
    public Dictionary<string,Pawn> Pawns { get; } = new(StringComparer.Ordinal);
    public string DirectoryPath { get; }
    public BattleDocumentDescriptor? InitialSnapshot { get; private set; }
    public string TemplateName => "GuildBattle_"+scope.BattleId+"_template";
    public string InitialName => "GuildBattle_"+scope.BattleId+"_initial";
    public string FinalName => "GuildBattle_"+scope.BattleId+"_final";

    public BattleScenario(BattleProfileV1 profile,BattleScopeMessage scope,string stateDirectory)
    {
        if(BattleGameContext.Role!=BattleMapRole.Executor || !Equals(BattleGameContext.Scope,scope) || RimlinkSession.Connection is not null || RimlinkSession.Active
            || profile.Fighters.Any(f=>f.BaselinerAppearanceVersion!=BattleManifestContracts.StandardBaselinerAppearanceVersion))throw Invalid();
        this.profile=profile.Clone();this.scope=scope.Clone();
        DirectoryPath=Path.Combine(Path.GetFullPath(stateDirectory),scope.BattleId,scope.Attempt.ToString(System.Globalization.CultureInfo.InvariantCulture),scope.Fence.ToString(System.Globalization.CultureInfo.InvariantCulture));
        NoLinks(DirectoryPath);Directory.CreateDirectory(DirectoryPath);
    }
    public void LoadTemplate(byte[] bytes)
    {
        RequireExecutor();
        if(Current.ProgramState!=ProgramState.Entry || Current.Game is not null || bytes.Length!=profile.Template.SizeBytes
            || !SHA256.HashData(bytes).AsSpan().SequenceEqual(profile.Template.Sha256.Span))throw Invalid();
        using(var input=new MemoryStream(bytes)) CheckDocument(XDocument.Load(input),requireScope:false);
        WriteOnce(GenFilePaths.FilePathForSavedGame(TemplateName),bytes);
        Reload(TemplateName);
    }
    public void CreateArenaAndReload()
    {
        RequireExecutor();
        if(Current.ProgramState!=ProgramState.Playing || !Current.Game.PlayerHasControl || Find.Maps.Count!=1)throw Invalid();
        Map=Find.CurrentMap;Find.TickManager.CurTimeSpeed=TimeSpeed.Paused;
        if(Map.Size.x!=profile.Arena.Width || Map.Size.z!=profile.Arena.Height || Current.Game.GetComponent<Outposts.PawnCustodyRegistry>().HasRecords)throw Invalid();
        Map.GetComponent<MapComponent_BattleScope>().Bind(scope);
        foreach(var pawn in Map.mapPawns.AllPawnsSpawned.ToArray())pawn.Destroy(DestroyMode.Vanish);
        foreach(var thing in Map.listerThings.AllThings.ToArray())
        {if(thing.def.destroyable)thing.Destroy(DestroyMode.Vanish);else thing.DeSpawn();}
        var concrete=DefDatabase<TerrainDef>.GetNamed("Concrete");
        for(int x=0;x<Map.Size.x;x++)for(int z=0;z<Map.Size.z;z++)
        {
            var cell=new IntVec3(x,0,z);Map.roofGrid.SetRoof(cell,null);Map.terrainGrid.SetTerrain(cell,concrete);Map.snowGrid.SetDepth(cell,0);
            if(x==0 || z==0 || x==Map.Size.x-1 || z==Map.Size.z-1)
                GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.Wall,DefDatabase<ThingDef>.GetNamed("BlocksGranite")),cell,Map);
        }
        Map.weatherManager.TransitionTo(WeatherDefOf.Clear);
        var factions=new Dictionary<BattleFighterSide,Faction>();
        foreach(var side in new[]{BattleFighterSide.Attacker,BattleFighterSide.Defender})
        {
            var faction=FactionGenerator.NewGeneratedFaction(new FactionGeneratorParms(DefDatabase<FactionDef>.GetNamed(RimlinkFactions.RaidersDefName),default,hidden:true));
            faction.Name="Guild battle "+side;Find.FactionManager.Add(faction);factions.Add(side,faction);
        }
        factions[BattleFighterSide.Attacker].SetRelationDirect(factions[BattleFighterSide.Defender],FactionRelationKind.Hostile,canSendHostilityLetter:false);
        using(var permit=BattleGameContext.AllowExecutorOrders())
        foreach(var spec in profile.Fighters)
        {
            if(spec.X<=0 || spec.Z<=0 || spec.X>=Map.Size.x-1 || spec.Z>=Map.Size.z-1)throw Invalid();
            var pawn=PawnGenerator.GeneratePawn(new PawnGenerationRequest(DefDatabase<PawnKindDef>.GetNamed(spec.PawnKindDef),factions[spec.Side],
                forceGenerateNewPawn:true,canGeneratePawnRelations:false,mustBeCapableOfViolence:true,allowAddictions:false,allowFood:false,forceBaselinerChance:1f,
                fixedBiologicalAge:spec.AgeYears,fixedChronologicalAge:spec.AgeYears,fixedGender:spec.Gender==BattleFighterGender.Male?Gender.Male:Gender.Female,
                forceNoIdeo:true,forceNoBackstory:true,forbidAnyTitle:true,forceNoGear:true));
            pawn.Name=new NameSingle(spec.FighterId);
            foreach(var hediff in pawn.health.hediffSet.hediffs.ToArray())pawn.health.RemoveHediff(hediff);
            pawn.story.traits.allTraits.Clear();pawn.story.bodyType=spec.Gender==BattleFighterGender.Male?BodyTypeDefOf.Male:BodyTypeDefOf.Female;
            NormalizeAppearance(pawn);
            foreach(var skill in pawn.skills.skills){skill.Level=skill.def==SkillDefOf.Shooting?spec.Shooting:skill.def==SkillDefOf.Melee?spec.Melee:8;skill.passion=Passion.None;skill.xpSinceLastLevel=0;}
            pawn.equipment.DestroyAllEquipment();pawn.apparel.DestroyAll();pawn.inventory.DestroyAll();
            foreach(var need in pawn.needs.AllNeeds)need.CurLevelPercentage=1f;
            var weapon=(ThingWithComps)ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed(spec.WeaponDef));
            weapon.TryGetComp<CompQuality>()?.SetQuality(QualityCategory.Normal,ArtGenerationContext.Outsider);pawn.equipment.AddEquipment(weapon);
            foreach(string name in spec.ApparelDefs)
            {
                var def=DefDatabase<ThingDef>.GetNamed(name);var apparel=(Apparel)ThingMaker.MakeThing(def,def.MadeFromStuff?ThingDefOf.Cloth:null);
                apparel.TryGetComp<CompQuality>()?.SetQuality(QualityCategory.Normal,ArtGenerationContext.Outsider);pawn.apparel.Wear(apparel);
            }
            GenSpawn.Spawn(pawn,new IntVec3(spec.X,0,spec.Z),Map);if(pawn.drafter is not null)pawn.drafter.Drafted=true;Pawns.Add(spec.FighterId,pawn);
        }
        foreach(var faction in factions.Values)LordMaker.MakeNewLord(faction,new LordJob_RimlinkGuests(),Map,Pawns.Values.Where(p=>p.Faction==faction));
        VerifyInitial();InitialSnapshot=Capture(InitialName,"initial",BattleDocumentKind.InitialSnapshot);Reload(InitialName);
    }
    public void AttachReloadedInitial()
    {
        RequireExecutor();
        if(!BattleGameContext.BindLoadedBattle(Current.Game))throw Invalid();Map=Find.CurrentMap;Pawns.Clear();
        foreach(var fighter in profile.Fighters)Pawns.Add(fighter.FighterId,Map.mapPawns.AllPawnsSpawned.Single(p=>p.Name?.ToStringShort==fighter.FighterId));
        Find.TickManager.CurTimeSpeed=TimeSpeed.Paused;VerifyInitial();
        byte[] bytes=File.ReadAllBytes(ArtifactPath("initial"));
        if(InitialSnapshot is null || bytes.Length!=InitialSnapshot.SizeBytes || !SHA256.HashData(bytes).AsSpan().SequenceEqual(InitialSnapshot.Sha256.Span))throw Invalid();
    }
    public BattlePawn[] ReadPawns()=>profile.Fighters.Select(spec=>
    {
        var pawn=Pawns[spec.FighterId];var position=pawn.PositionHeld;
        return new BattlePawn(spec.FighterId,(BattleSide)(int)spec.Side,position.x,position.z,!pawn.Dead,pawn.Downed,pawn.InMentalState,
            pawn.Spawned && !pawn.WorkTagIsDisabled(WorkTags.Violent) && pawn.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation)
                && pawn.health.capacities.CapableOf(PawnCapacityDefOf.Moving),pawn.health.summaryHealth.SummaryHealthPercent);
    }).ToArray();
    public void VerifyInitial()
    {
        RequireExecutor();
        if(Map!=Find.CurrentMap || Find.Maps.Count!=1 || Map.mapPawns.AllPawnsSpawned.Count!=8 || !Equals(Map.GetComponent<MapComponent_BattleScope>().Identity,scope))
            throw Invalid("initial-map:"+JsonSerializer.Serialize(new{SameMap=Map==Find.CurrentMap,Maps=Find.Maps.Count,Pawns=Map.mapPawns.AllPawnsSpawned.Count,Identity=Map.GetComponent<MapComponent_BattleScope>().Identity?.ToString()}));
        foreach(var spec in profile.Fighters)
        {
            var pawn=Pawns[spec.FighterId];
            VerifyAppearance(pawn);
            if(pawn.def.defName!=spec.RaceDef || pawn.kindDef.defName!=spec.PawnKindDef || pawn.Position!=new IntVec3(spec.X,0,spec.Z)
                || pawn.Dead || pawn.Downed || pawn.InMentalState || Math.Abs(pawn.health.summaryHealth.SummaryHealthPercent-1)>0.00001f
                || pawn.health.hediffSet.hediffs.Count!=0 || pawn.story.traits.allTraits.Count!=0 || pawn.inventory.innerContainer.Count!=0
                || pawn.needs.AllNeeds.Any(n=>Math.Abs(n.CurLevelPercentage-1f)>0.0001f)
                || Math.Abs(pawn.ageTracker.AgeBiologicalYearsFloat-spec.AgeYears)>0.0001f || pawn.gender!=(spec.Gender==BattleFighterGender.Male?Gender.Male:Gender.Female)
                || pawn.skills.GetSkill(SkillDefOf.Shooting).Level!=spec.Shooting || pawn.skills.GetSkill(SkillDefOf.Melee).Level!=spec.Melee
                || pawn.equipment.Primary?.def.defName!=spec.WeaponDef || !pawn.apparel.WornApparel.Select(a=>a.def.defName).Order(StringComparer.Ordinal).SequenceEqual(spec.ApparelDefs))
                throw Invalid("initial-fighter:"+JsonSerializer.Serialize(new{spec.FighterId,Def=pawn.def.defName,Kind=pawn.kindDef.defName,X=pawn.Position.x,Z=pawn.Position.z,
                    pawn.Dead,pawn.Downed,pawn.InMentalState,Health=pawn.health.summaryHealth.SummaryHealthPercent,Hediffs=pawn.health.hediffSet.hediffs.Select(h=>h.def.defName).ToArray(),
                    Traits=pawn.story.traits.allTraits.Count,Inventory=pawn.inventory.innerContainer.Count,Needs=pawn.needs.AllNeeds.Select(n=>new{Def=n.def.defName,Level=n.CurLevelPercentage}).ToArray(),
                    Genes=pawn.genes?.GenesListForReading.Count,Age=pawn.ageTracker.AgeBiologicalYearsFloat,Gender=pawn.gender.ToString(),Shooting=pawn.skills.GetSkill(SkillDefOf.Shooting).Level,
                    Melee=pawn.skills.GetSkill(SkillDefOf.Melee).Level,Weapon=pawn.equipment.Primary?.def.defName,Apparel=pawn.apparel.WornApparel.Select(a=>a.def.defName).ToArray()}));
            foreach(var gear in pawn.apparel.WornApparel.Cast<Thing>().Append(pawn.equipment.Primary))
                if(gear is null || gear.HitPoints!=gear.MaxHitPoints || gear.TryGetQuality(out var quality) && quality!=QualityCategory.Normal)
                    throw Invalid("initial-gear:"+spec.FighterId+":"+gear?.def.defName+":"+gear?.HitPoints+"/"+gear?.MaxHitPoints);
        }
        if(!Pawns[profile.Fighters[0].FighterId].Faction.HostileTo(Pawns[profile.Fighters.First(f=>f.Side!=profile.Fighters[0].Side).FighterId].Faction))throw Invalid("initial-hostility");
    }
    private static void NormalizeAppearance(Pawn pawn)
    {
        if(pawn.genes is null)throw Invalid("initial-genes-missing");
        if(ModsConfig.BiotechActive)pawn.genes.SetXenotype(XenotypeDefOf.Baseliner);
        foreach(var gene in pawn.genes.GenesListForReading.ToArray())pawn.genes.RemoveGene(gene);
        foreach(string name in StandardGenes)pawn.genes.AddGene(DefDatabase<GeneDef>.GetNamed(name),xenogene:false);
        pawn.story.skinColorOverride=null;pawn.story.SkinColorBase=StandardSkin;pawn.story.HairColor=StandardHair;
        VerifyAppearance(pawn);
    }
    private static void VerifyAppearance(Pawn pawn)
    {
        if(!ExactGenes(pawn))throw Invalid("initial-genes:"+pawn.Name?.ToStringShort+":"+string.Join(",",pawn.genes?.GenesListForReading.Select(g=>g.def.defName)??[]));
        var skin=pawn.story.SkinColorBase;var hair=pawn.story.HairColor;
        if(!ExactGenes(pawn) || pawn.story.skinColorOverride is not null || !SameColor(skin,StandardSkin) || !SameColor(hair,StandardHair))
            throw Invalid("initial-colors:"+pawn.Name?.ToStringShort+":"+skin+":"+hair);
    }
    private static bool ExactGenes(Pawn pawn)=>pawn.genes is{} genes && genes.Endogenes.Count==2 && genes.Xenogenes.Count==0 && genes.GenesListForReading.Count==2
        && !genes.UniqueXenotype && !genes.hybrid && (ModsConfig.BiotechActive?genes.Xenotype==XenotypeDefOf.Baseliner:genes.Xenotype is null)
        && genes.Endogenes.All(g=>g.Active && g.GetType()==typeof(Gene) && g.def.GetType()==typeof(GeneDef)
            && string.Equals(g.def.modContentPack?.PackageId,"ludeon.rimworld",StringComparison.OrdinalIgnoreCase))
        && genes.Endogenes.Select(g=>g.def.defName).Order(StringComparer.Ordinal).SequenceEqual(StandardGenes);
    private static bool SameColor(Color actual,Color expected)=>float.IsFinite(actual.r) && float.IsFinite(actual.g) && float.IsFinite(actual.b) && float.IsFinite(actual.a)
        && Math.Abs(actual.r-expected.r)<=0.00001f && Math.Abs(actual.g-expected.g)<=0.00001f && Math.Abs(actual.b-expected.b)<=0.00001f && Math.Abs(actual.a-expected.a)<=0.00001f;
    public BattleDocumentDescriptor SaveFinal()=>Capture(FinalName,"final",BattleDocumentKind.FinalSnapshot);
    public string ArtifactPath(string kind)
    {
        if(kind is not ("initial" or "final" or "simulation" or "view" or "envelope" or "receipts"))throw Invalid();
        string path=Path.Combine(DirectoryPath,kind+".artifact");NoLinks(path);return path;
    }
    private BattleDocumentDescriptor Capture(string name,string kind,BattleDocumentKind documentKind)
    {
        RequireExecutor();if(Map!=Find.CurrentMap)throw Invalid();
        using(BattleGameContext.AllowBattleSave(name))GameDataSaveLoader.SaveGame(name);
        string source=GenFilePaths.FilePathForSavedGame(name);NoLinks(source);
        var size=new FileInfo(source).Length;if(size is <1 or >67108864)throw Invalid();
        byte[] bytes=File.ReadAllBytes(source);using(var input=new MemoryStream(bytes))CheckDocument(XDocument.Load(input),true);
        WriteOnce(ArtifactPath(kind),bytes);
        return new(){Kind=documentKind,SizeBytes=bytes.Length,Sha256=ByteString.CopyFrom(SHA256.HashData(bytes))};
    }
    internal static void CheckDocument(XDocument document,bool requireScope)
    {
        if(document.Root?.Name.LocalName!="savegame" || document.Root.Element("game") is not {} game
            || game.Descendants().Any(e=>e.Name.LocalName=="rimlinkOutposts" || e.Name.LocalName is "rimlinkPawnCustody" or "rimlinkParcelReceipts" or "rimlinkRemovedOperations" or "rimlinkReturnedOperations" or "rimlinkActiveRaids" && e.HasElements || e.Name.LocalName=="rimlinkSnapshotId" && !string.IsNullOrEmpty(e.Value))
            || requireScope && game.Descendants("rimlinkBattleScope").Count()!=1)throw Invalid();
    }
    private static void WriteOnce(string path,byte[] bytes)
    {
        NoLinks(path);
        if(File.Exists(path)){if(!File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes))throw Invalid();return;}
        if(File.Exists(path+".part"))throw Invalid();Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using(var output=new FileStream(path+".part",FileMode.CreateNew,FileAccess.Write,FileShare.None)){output.Write(bytes);output.Flush(true);}
        File.Move(path+".part",path,false);
    }
    private static void Reload(string name)
    {GlobalTextureAtlasManager.FreeAllRuntimeAtlases();PortraitsCache.Clear();using(BattleGameContext.AllowLoad(name))GameDataSaveLoader.LoadGame(name);}
    private void RequireExecutor()
    {if(BattleGameContext.Role!=BattleMapRole.Executor || !Equals(BattleGameContext.Scope,scope) || RimlinkSession.Active || RimlinkSession.Connection is not null)throw Invalid();}
    private static void NoLinks(string path)
    {for(string? current=Path.GetFullPath(path);current is not null;current=Path.GetDirectoryName(current))if((File.Exists(current)||Directory.Exists(current)) && (File.GetAttributes(current)&FileAttributes.ReparsePoint)!=0)throw Invalid();}
    private static InvalidDataException Invalid(string detail="")=>new("battle-native-scenario-invalid"+(detail.Length==0?"":":"+detail));
}
