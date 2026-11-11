using System.Security.Cryptography;
using System.Text.Json;
using Rimlink.Client.GuildCapture;
using Rimlink.Protocol.V1;
using Rimlink.Client.UI;
using RimWorld;
using Verse;

namespace Rimlink.Client.Game.GuildCapture;

internal static class BattleExecutorStartup
{
    private const string CaptureArgument="-rimlinkExecutorCapture=";
    private const string TemplateSave="RimlinkArbiterTemplate";
    private static string? configuration,capture,captureStatus;
    private static bool templating;
    internal static bool WritingTemplate {get;private set;}
    private static bool started;
    private static BattleExecutorOperator? runtime;
    public static bool Selected {get;private set;}
    public static string Status=>captureStatus??runtime?.Status??(started?"RecoveryRequired":"Loading");
    internal static bool Select(IReadOnlyList<string> arguments)
    {
        // The operator's preparation run: measure this very installation and write the arbiter profile, nothing else.
        var captures=arguments.Where(a=>a.StartsWith(CaptureArgument,StringComparison.OrdinalIgnoreCase)).Select(a=>a[CaptureArgument.Length..]).ToArray();
        Selected=captures.Length!=0 || arguments.Any(a=>a.StartsWith("-rimlinkExecutorConfig=",StringComparison.OrdinalIgnoreCase));
        if(!Selected)return false;
        if(captures.Length!=0)
        {
            if(captures.Length==1 && Path.IsPathFullyQualified(captures[0]) && !arguments.Any(a=>a.StartsWith("-rimlinkExecutorConfig=",StringComparison.OrdinalIgnoreCase)))capture=captures[0];
            else{started=true;Log.Error("[Rimlink] arbiter profile capture argument refused");}
            return true;
        }
        try{configuration=BattleExecutorSettings.ConfigurationArgument(arguments)??throw new InvalidDataException();}
        catch(Exception error)when(error is InvalidDataException or ArgumentException)
        {started=true;Log.Error("[Rimlink] executor configuration argument refused: "+error.GetType().Name);}
        return true;
    }
    internal static void Frame()
    {
        if(templating){TemplateFrame();return;}
        if(!Selected || started || Current.ProgramState!=ProgramState.Entry || Current.Game is not null || LongEventHandler.AnyEventNowOrWaiting)return;
        if(!ModpackActions.Content.Ready.IsCompletedSuccessfully)return;
        if(capture is not null && !File.Exists(Path.Combine(capture,"template.rws")))
        {
            // No template yet: make one the way the main menu's quick test does. Only the 250x250 map survives into a battle.
            templating=true;Log.Message("[Rimlink] arbiter template: generating a 250x250 map");
            LongEventHandler.QueueLongEvent(()=>{Root_Play.SetupForQuickTestPlay();PageUtility.InitGameStart();},"GeneratingMap",true,GameAndMapInitExceptionHandlers.ErrorWhileGeneratingMap);
            return;
        }
        started=true;
        if(capture is not null){Capture(capture);return;}
        _=Task.Run(async()=>
        {
            try{runtime=new(BattleExecutorSettings.Read(configuration!));await runtime.Run();}
            catch(Exception error)
            {
                if(runtime is not null)runtime.Status="RecoveryRequired";
                MainThread.Post(()=>Log.Error("[Rimlink] executor requires recovery; immutable assignment and artifacts retained: "+error));
            }
        });
    }
    private static void TemplateFrame()
    {
        if(LongEventHandler.AnyEventNowOrWaiting)return;
        // A failed generation returns to the menu on its own: do not loop.
        if(Current.ProgramState==ProgramState.Entry){templating=false;started=true;captureStatus="ProfileCaptureFailed";Log.Error("[Rimlink] arbiter template: map generation failed");return;}
        if(Current.ProgramState!=ProgramState.Playing || Find.CurrentMap is null)return;
        try
        {
            if(Find.Maps.Count!=1 || Find.CurrentMap.Size.x!=250 || Find.CurrentMap.Size.z!=250)throw new InvalidDataException("the generated map is not 250x250");
            Find.TickManager.CurTimeSpeed=TimeSpeed.Paused;
            WritingTemplate=true;
            try{GameDataSaveLoader.SaveGame(TemplateSave);}finally{WritingTemplate=false;}
            string saved=GenFilePaths.FilePathForSavedGame(TemplateSave);
            Directory.CreateDirectory(capture!);File.Copy(saved,Path.Combine(capture!,"template.rws"));File.Delete(saved);
            Log.Message("[Rimlink] arbiter template written");templating=false;GenScene.GoToMainMenu();
        }
        catch(Exception error){templating=false;started=true;captureStatus="ProfileCaptureFailed";Log.Error("[Rimlink] arbiter template failed: "+error.Message);}
    }
    // Writes profile.pb, profile-archive, profile-capture.json and the executor-settings.json skeleton next to the operator's
    // template.rws. The roots are wherever this installation keeps the game and each loaded mod: Steam folders included.
    private static void Capture(string directory)
    {
        try
        {
            directory=Path.GetFullPath(directory);
            string template=Path.Combine(directory,"template.rws"),profileFile=Path.Combine(directory,"profile.pb"),archive=Path.Combine(directory,"profile-archive");
            if(File.Exists(profileFile) || Directory.Exists(archive))throw new InvalidDataException("the folder already holds a profile: capture into an empty folder with only template.rws");
            var roots=new List<BattleRuntimeRootPath>{new(BattleInventoryRoot.Game,"game",Path.GetDirectoryName(typeof(Pawn).Assembly.Location)!),
                new(BattleInventoryRoot.Runtime,"runtime",Path.GetDirectoryName(typeof(object).Assembly.Location)!)};
            int index=0;foreach(var mod in LoadedModManager.RunningMods)roots.Add(new(BattleInventoryRoot.Mod,"mod-"+(index++),Path.GetFullPath(mod.RootDir)));
            var paths=roots.ToArray();var facts=BattleRuntimeCapture.Measure(new(){Roots=paths});
            var profile=BattleProfileCapture.Create(facts,paths,template,archive,profileFile);
            string sha=Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(profileFile))),digest=Convert.ToHexStringLower(profile.Digest.Span);
            var json=new JsonSerializerOptions{WriteIndented=true};
            // Server-specific fields stay empty: the operator fills them in before the arbiter run.
            File.WriteAllText(Path.Combine(directory,"executor-settings.json"),JsonSerializer.Serialize(new BattleExecutorSettings{Version=1,ProfilePath=profileFile,ProfileSha256=sha,
                ProfileDigest=digest,ArchiveRoot=archive,StateDirectory=Path.Combine(GenFilePaths.SaveDataFolderPath,"RimlinkExecutor"),Roots=paths},json));
            File.WriteAllText(Path.Combine(directory,"profile-capture.json"),JsonSerializer.Serialize(new{Version=1,ProfileId=profile.Profile.ProfileId,ProfileSha256=sha,ProfileDigest=digest,
                GameVersion=RimWorld.VersionControl.CurrentVersionStringWithRev,Mods=facts.Mods.Select(m=>new{m.LoadIndex,m.PackageId,m.Version}),facts.ActiveDlc,
                Documents=new{Profile=profileFile,Inventory=Artifact(profile.Profile.Inventory),Config=Artifact(profile.Inventory.Config),Defs=Artifact(profile.Inventory.LoadedDefs),
                    ViewerCompatibility=Artifact(profile.Profile.ViewerCompatibility),Template=Artifact(profile.Profile.Template)}},json));
            captureStatus="ProfileCaptured";Log.Message("[Rimlink] arbiter profile captured: digest "+digest+", "+facts.Mods.Count+" mods, written to "+directory);
            string Artifact(BattleDocumentDescriptor d)=>Path.Combine(archive,"guild-profiles",((int)d.Kind).ToString(System.Globalization.CultureInfo.InvariantCulture),Convert.ToHexStringLower(d.Sha256.Span)+".artifact");
        }
        catch(Exception error){captureStatus="ProfileCaptureFailed";Log.Error("[Rimlink] arbiter profile capture failed: "+error.Message);}
    }
}
