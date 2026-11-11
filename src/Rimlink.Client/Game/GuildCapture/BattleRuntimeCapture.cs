using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using Google.Protobuf;
using HarmonyLib;
using Rimlink.Client.GuildCapture;
using Rimlink.Client.UI;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Verse;

namespace Rimlink.Client.Game.GuildCapture;

public static class BattleRuntimeCapture
{
    private static readonly Func<string,string,string> SettingsFile=AccessTools.MethodDelegate<Func<string,string,string>>(AccessTools.Method(typeof(LoadedModManager),"GetSettingsFilename"));

    internal static BattleRuntimeRootPath[] ViewerRoots(BattleExecutorProfile bundle)
    {
        var inventory = bundle.Inventory;
        var installed = LoadedModManager.RunningMods.ToDictionary(mod => ModpackActions.Normalize(mod.PackageId), StringComparer.Ordinal);
        if (installed.Count != inventory.Mods.Count) throw new InvalidDataException("loaded mods do not match the battle profile");
        var roots = new List<BattleRuntimeRootPath>();
        foreach (var mod in inventory.Mods)
        {
            if (!installed.TryGetValue(mod.PackageId, out var local)) throw new InvalidDataException("required battle mod is absent");
            roots.Add(new(BattleInventoryRoot.Mod, mod.RootId, Path.GetFullPath(local.RootDir)));
        }
        var engineRoots = inventory.Files.Select(file => (file.Root, file.RootId))
            .Concat(bundle.Profile.ExcludedRuntimePaths.Select(path => (path.Root, path.RootId))).Distinct().ToArray();
        foreach (var kind in new[] { BattleInventoryRoot.Game, BattleInventoryRoot.Runtime })
        {
            var matches = engineRoots.Where(root => root.Root == kind).ToArray();
            if (matches.Length != 1) throw new InvalidDataException("battle profile requires a different engine layout");
            var assembly = kind == BattleInventoryRoot.Game ? typeof(Pawn).Assembly : typeof(object).Assembly;
            var directory = Path.GetDirectoryName(assembly.Location);
            if (string.IsNullOrWhiteSpace(directory)) throw new InvalidDataException("battle engine path is unavailable");
            roots.Add(new(kind, matches[0].RootId, directory));
        }
        return roots.ToArray();
    }
    public static BattleRuntimeFacts Measure(BattleExecutorSettings settings,bool viewer=false)
    {
        if (!ModpackActions.Content.Ready.IsCompletedSuccessfully || !viewer && ServerSettings.Active is not null
            || viewer && (!RimlinkSession.Active || RimlinkSession.Login?.GuildVersion!=1 || BattleGameContext.Active)) throw new InvalidDataException("battle runtime content scan or independent settings are unavailable");
        var content=ModpackActions.Content.Ready.GetAwaiter().GetResult();
        var roots=settings.Roots.Where(r=>r.Kind==BattleInventoryRoot.Mod).ToDictionary(r=>Path.GetFullPath(r.Path),r=>r.Id,
            OperatingSystem.IsWindows()?StringComparer.OrdinalIgnoreCase:StringComparer.Ordinal);
        var mods=LoadedModManager.RunningMods.Select((m,index)=>new BattleModInventoryEntry{LoadIndex=index,PackageId=ModpackActions.Normalize(m.PackageId),
            Version=BattleRuntimeFacts.NormalizeVersion(m.ModMetaData?.ModVersion),RootId=roots.GetValueOrDefault(Path.GetFullPath(m.RootDir))??throw new InvalidDataException("running mod has no explicit inventory root")}).ToArray();
        if (mods.Length is <1 or >256 || mods.Select(m=>m.PackageId).Distinct(StringComparer.Ordinal).Count()!=mods.Length) throw new InvalidDataException("ambiguous loaded mod inventory");
        var settingsRows=ModpackActions.RunningModSettings().OrderBy(s=>s.PackageId,StringComparer.Ordinal).ThenBy(s=>s.Handle,StringComparer.Ordinal)
            .Where(s=>s.PackageId!=ClientInfo.ModPackageId).Select(s=>new{s.PackageId,s.Handle,s.Hash}).ToArray();
        ValidateOwnOperationalSettings();
        var debug=typeof(DebugSettings).GetFields(BindingFlags.Public|BindingFlags.Static).Where(f=>!f.IsLiteral).OrderBy(f=>f.Name,StringComparer.Ordinal)
            .Select(f=>new{Name=f.Name,Type=f.FieldType.FullName,Value=Simple(f.GetValue(null))}).ToArray();
        byte[] config=JsonSerializer.SerializeToUtf8Bytes(new{Format="rimlink-battle-config-v1",Settings=settingsRows,Debug=debug,
            RimlinkOperationalKeys=new[]{"hiddenInfluenceServers","letterPause","servers"}});
        int count=0;
        var packs=LoadedModManager.RunningMods.Select((m,index)=>new
        {
            Position=index,Package=ModpackActions.Normalize(m.PackageId),Version=BattleRuntimeFacts.NormalizeVersion(m.ModMetaData?.ModVersion),
            Content=content.TryGetValue(ModpackActions.Normalize(m.PackageId),out var info)?info.FilesHash:throw new InvalidDataException("loaded content fingerprint is missing"),
            Defs=m.AllDefs.Select(d=>d.GetType().FullName+":"+d.defName).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Select(d=>
            {if(++count>65536)throw new InvalidDataException("loaded definition inventory exceeds bound");return d;}).ToArray()
        }).ToArray();
        byte[] defs=JsonSerializer.SerializeToUtf8Bytes(new{Format="rimlink-battle-loaded-defs-v2",Packs=packs});
        if(config.Length>BattleManifestContracts.InventoryBytes || defs.Length>BattleManifestContracts.InventoryBytes)throw new InvalidDataException("measured battle documents exceed bounds");
        return new(mods,Outposts.OutpostManifestCapture.ActiveDlc(),config,defs,typeof(Pawn).Assembly.Location,typeof(object).Assembly.Location,
            typeof(BattleRules).Assembly.Location,typeof(BattleRuntimeCapture).Assembly.Location);
    }
    private static object? Simple(object? value)=>value is null or bool or int or long or float or double or string?value:throw new InvalidDataException("unknown debug configuration value type");
    private static void ValidateOwnOperationalSettings()
    {
        foreach(var mod in LoadedModManager.ModHandles.Where(m=>ModpackActions.Normalize(m.Content.PackageId)==ClientInfo.ModPackageId))
        {
            if(mod.GetType()!=typeof(RimlinkMod))throw new InvalidDataException("unknown Rimlink settings handle");
            string path=SettingsFile(mod.Content.FolderName,mod.GetType().Name);if(!File.Exists(path))continue;
            using var input=XmlReader.Create(path,new XmlReaderSettings{DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=8*1024*1024});
            var document=XDocument.Load(input);
            var blocks=document.Root?.Elements().ToArray()??[];
            if(document.Root?.Name.LocalName!="SettingsBlock" || blocks.Length!=1 || blocks[0].Name.LocalName!="ModSettings"
                || blocks[0].Elements().Any(e=>e.Name.LocalName is not ("servers" or "letterPause" or "hiddenInfluenceServers"))
                || blocks[0].Elements().GroupBy(e=>e.Name.LocalName,StringComparer.Ordinal).Any(g=>g.Count()!=1))throw new InvalidDataException("unknown Rimlink configuration projection key");
        }
    }
}
