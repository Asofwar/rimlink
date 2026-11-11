using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using RimBridgeServer.Sdk;
using RimWorld;
using Rimlink.Client.Game;
using Rimlink.Client.Game.GuildCapture;
using Rimlink.Client.GuildCapture;
using Rimlink.Protocol.V1;
using Verse;

namespace Rimlink.BridgeTools;

public sealed class GuildProfileCaptureTools
{
    [Tool("rimlink/guild_profile_capture",Description="Measure the real independent runtime and write a fixed 4x4 profile/reference closure under the new owned stand. Preflight only; never native HOME or Product qualification")]
    public async Task<object> Capture(IRimBridgeContext ctx,
        [ToolParameter(Description="Canonical root of the new owned stand, before bootstrap",Required=true)]string standRoot)
    {
        try
        {
            standRoot=Path.GetFullPath(standRoot);NoLinks(standRoot);
            string game=Path.Combine(standRoot,"game"),profileFile=Path.Combine(standRoot,"profile.pb"),archive=Path.Combine(standRoot,"profile-archive");
            string template=Path.Combine(standRoot,"evidence","template.rws"),evidenceFile=Path.Combine(standRoot,"evidence","profile-capture.json");
            if(File.Exists(Path.Combine(standRoot,"bootstrap.json")) || File.Exists(profileFile) || File.Exists(evidenceFile))throw new InvalidDataException("profile capture requires a new preflight stand");
            var measured=await Bridge.OnMain(ctx,()=>
            {
                if(RimlinkSession.Active || RimlinkSession.Connection is not null || BattleGameContext.Active || Current.ProgramState!=ProgramState.Entry || Current.Game is not null
                    || !Same(GenFilePaths.SaveDataFolderPath,Path.Combine(standRoot,"saves-executor")) || !Within(typeof(Pawn).Assembly.Location,game)
                    || !Within(typeof(BattleRuntimeCapture).Assembly.Location,Path.Combine(game,"Mods","Rimlink")))throw new InvalidDataException("profile capture process is outside the owned stand");
                if(DefDatabase<PawnKindDef>.GetNamedSilentFail("Colonist") is null || DefDatabase<TerrainDef>.GetNamedSilentFail("Concrete") is null
                    || new[]{"Human","Gun_BoltActionRifle","Apparel_BasicShirt","Apparel_Pants","Cloth","Wall"}.Any(name=>DefDatabase<ThingDef>.GetNamedSilentFail(name) is null))
                    throw new InvalidDataException("standard battle definition missing");
                var roots=new List<BattleRuntimeRootPath>{new(BattleInventoryRoot.Game,"game",game),new(BattleInventoryRoot.Runtime,"runtime",Path.GetDirectoryName(typeof(object).Assembly.Location)!)};
                int i=0;foreach(var mod in LoadedModManager.RunningMods)
                {
                    if(!Within(mod.RootDir,game))throw new InvalidDataException("loaded mod is not in the owned game copy");
                    roots.Add(new(BattleInventoryRoot.Mod,"mod-"+(i++),Path.GetFullPath(mod.RootDir)));
                }
                var paths=roots.ToArray();return(Facts:BattleRuntimeCapture.Measure(new(){Roots=paths}),Roots:paths);
            });
            var profile=BattleProfileCapture.Create(measured.Facts,measured.Roots,template,archive,profileFile);
            var document=new{Version=1,Kind="GuildProfileCapturePreflight",NativeAcceptance="NOT RUN",ProfileId=profile.Profile.ProfileId,
                ProfileSha256=Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(profileFile))),ProfileDigest=Convert.ToHexStringLower(profile.Digest.Span),
                InventorySha256=Convert.ToHexStringLower(profile.Profile.Inventory.Sha256.Span),Files=profile.Inventory.Files.Count,
                TemplateSha256=Convert.ToHexStringLower(profile.Profile.Template.Sha256.Span),measured.Roots,Mods=measured.Facts.Mods,measured.Facts.ActiveDlc};
            NoLinks(evidenceFile);using(var output=new FileStream(evidenceFile,FileMode.CreateNew,FileAccess.Write,FileShare.None)){output.Write(JsonSerializer.SerializeToUtf8Bytes(document));output.Flush(true);}
            return new{success=true,phase="ProfileCapturedPreflight",nativeAcceptance="NOT RUN",document.ProfileId,document.ProfileSha256,document.ProfileDigest,document.Files};
        }
        catch(Exception error){return Bridge.Fail(error.Message);}
    }
    [Tool("rimlink/guild_profile_verify",Description="Logged-in player: check this installation against a captured arbiter profile exactly as the battle viewer does before opening a battle")]
    public async Task<object> Verify(IRimBridgeContext ctx,
        [ToolParameter(Description="Folder written by -rimlinkExecutorCapture (profile.pb, profile-archive, profile-capture.json)",Required=true)]string directory)
    {
        try
        {
            directory=Path.GetFullPath(directory);
            using var capture=JsonDocument.Parse(File.ReadAllBytes(Path.Combine(directory,"profile-capture.json")));
            var profile=BattleExecutorProfile.Load(new(){ProfilePath=Path.Combine(directory,"profile.pb"),ArchiveRoot=Path.Combine(directory,"profile-archive"),
                ProfileSha256=capture.RootElement.GetProperty("ProfileSha256").GetString()!,ProfileDigest=capture.RootElement.GetProperty("ProfileDigest").GetString()!});
            return await Bridge.OnMain(ctx,()=>
            {
                var roots=BattleRuntimeCapture.ViewerRoots(profile);
                var actual=BattleRuntimeCapture.Measure(new(){Roots=roots},viewer:true);
                profile.VerifyViewerRuntime(actual);
                return (object)new{success=true,compatible=true,profileDigest=Convert.ToHexStringLower(profile.Digest.Span),
                    game=Path.GetDirectoryName(actual.GameBuildPath),client=actual.ClientBuildPath,mods=roots.Where(r=>r.Kind==BattleInventoryRoot.Mod).Select(r=>r.Path).ToArray()};
            });
        }
        catch(Exception error){return Bridge.Fail(error.Message);}
    }
    private static bool Same(string left,string right)=>Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)).Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),OperatingSystem.IsWindows()?StringComparison.OrdinalIgnoreCase:StringComparison.Ordinal);
    private static bool Within(string file,string root)=>Same(file,root)||Path.GetFullPath(file).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root))+Path.DirectorySeparatorChar,OperatingSystem.IsWindows()?StringComparison.OrdinalIgnoreCase:StringComparison.Ordinal);
    private static void NoLinks(string path)
    {
        for(string? parent=Path.GetFullPath(path);parent is not null;parent=Path.GetDirectoryName(parent))
            if((Directory.Exists(parent)||File.Exists(parent)) && (File.GetAttributes(parent)&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException("profile path is aliased");
    }
}
