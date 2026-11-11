using System.Diagnostics;
using System.Security.Cryptography;
using System.Xml.Linq;
using Google.Protobuf;
using Rimlink.Client.GuildCapture;
using Rimlink.Client.Net;
using Rimlink.Client.UI;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using UnityEngine;
using Verse;
using RimWorld;

namespace Rimlink.Client.Game.GuildCapture;

// The canonical HOME lifecycle owns the native map; battle controls use a separate scoped link.
public sealed class BattlePreparedViewer
{
    private static BattlePreparedViewer? current;
    public static bool Busy{get;private set;}
    public static bool Viewing=>current is not null && BattleGameContext.Role==BattleMapRole.Viewer;
    private readonly BattleViewerMarkerV1 marker;
    private readonly string homeFile;
    private string? failure;
    private BattleRunningViewer? running;
    private BattlePreparedViewer(BattleViewerMarkerV1 marker,string homeFile)
    {this.marker=marker;this.homeFile=homeFile;}
    internal string HomeFile=>homeFile;
    private static string DirectoryFor(ServerEndpoint endpoint,long player)=>Path.Combine(RimlinkSession.SaveDir(endpoint,player),"guild-battle");
    private static string MarkerPath(ServerEndpoint endpoint,long player)=>Path.Combine(DirectoryFor(endpoint,player),"viewer.rlb");
    public static bool HasRetainedMarker(ServerEndpoint endpoint,long player)
    {
        string path=MarkerPath(endpoint,player);NoLinks(path);return File.Exists(path);
    }
    internal static bool PrepareEntryLogin()
    {
        if(!BattleGameContext.Active)return true;
        if(!Viewing || Busy || !BattleGameContext.ReleaseViewerAtEntry())return false;
        current!.running?.Stop();current=null;return true;
    }
    internal static BattlePreparedViewer? ReadRetained(ServerEndpoint endpoint,long player)
    {
        string path=MarkerPath(endpoint,player);NoLinks(path);
        if(new BattleViewerMarkerStore(path).Read() is not{} retained)return null;
        string file=Path.Combine(DirectoryFor(endpoint,player),"home_"+retained.Home.SnapshotId+".rws");NoLinks(file);
        return new(retained,file);
    }
    internal async Task RecoverRetainedHome(ServerEndpoint endpoint,RimlinkConnection owner,LoginReply login,CancellationToken ct)
    {
        await MainThread.Get(()=>
        {
            ct.ThrowIfCancellationRequested();RequireHomeOwner(owner,login);
            if(Busy || current is not null)throw Invalid();
            string path=MarkerPath(endpoint,login.Player.PlayerId);NoLinks(path);
            BattleGameContext.RecoverViewerAtEntry(marker,new(path));current=this;return true;
        });
        await ReturnHome(ct);
    }
    public static async Task Enter(BattleScopeMessage scope,BattleExecutorProfile profile,BattleRuntimeRootPath[] roots,CancellationToken ct)
    {
        scope=scope.Clone();new BattleMapGuard().BeginExecutor(scope);roots=roots.ToArray();
        var owner=await MainThread.Get(()=>
        {
            ct.ThrowIfCancellationRequested();
            if(Busy || current is not null || RimlinkSession.Connection is not{} connection
                || connection.Login is not{} login || RimlinkSession.Endpoint is not{} endpoint)throw Invalid();
            RequireHomeOwner(connection,login);
            var home=new GuildColonyScope{WorldInstanceId=scope.WorldInstanceId,RestoreBoundary=scope.RestoreBoundary,PlayerId=login.Player.PlayerId,
                SaveGeneration=login.SaveGeneration,SaveWriteEpoch=login.SaveWriteEpoch};
            BattleGameContext.RequireIdleViewerHome(home);
            if(HasRetainedMarker(endpoint,login.Player.PlayerId))throw Invalid();
            var actual=BattleRuntimeCapture.Measure(new(){Roots=roots},viewer:true);profile.VerifyViewerRuntime(roots,actual);
            var component=Current.Game.GetComponent<RimlinkGameComponent>();
            component.OutpostRecovery=new(){Metadata=GuildDeliveryEvidence.PrepareRecipient(component.OutpostRecovery.Metadata,home,component.SaveGeneration,
                component.SaveWriteEpoch,component.OutpostLoadFailure is not null,Current.Game.GetComponent<Outposts.PawnCustodyRegistry>().HasRecords)};
            Find.TickManager.CurTimeSpeed=TimeSpeed.Paused;Busy=true;
            return (Connection:connection,Login:login,Game:Current.Game,Endpoint:endpoint,HomeScope:home);
        });
        string directory=DirectoryFor(owner.Endpoint,owner.Login.Player.PlayerId),homeFile="";
        BattleRunningViewer? resumed = null;
        try
        {
            NoLinks(directory);Directory.CreateDirectory(directory);
            var accepted=await SaveSync.SaveExactAsync(snapshot=>
            {
                homeFile=Path.Combine(directory,"home_"+snapshot.Id+".rws");
                CopyOnce(snapshot.Path,homeFile,snapshot.Meta.RawSha256??throw Invalid());
            });
            if(!accepted.Uploaded || accepted.Info is null || homeFile.Length==0)throw Invalid();
            var home=BattleCanonicalHome.ReadAccepted(homeFile,accepted.Info,owner.HomeScope);
            await Wait(()=>
            {
                RequireHomeOwner(owner.Connection,owner.Login);
                return !RimlinkSession.RecoveryNeeded && !SaveSync.UploadInFlight;
            },60,ct);
            var request=new BattlePreparedViewRequest{Scope=scope,Home=home,HomeVersion=accepted.Info.Version,ProfileDigest=profile.Digest};
            var client=new GuildCaptureClient(owner.Connection);var view=await client.PreparedView(request,ct);
            var manifest=BattlePreparedViewDownload.Validate(request,view);
            string initial=Path.Combine(directory,"initial_"+scope.BattleId+"_"+manifest.ProposalSequence+"_"+Convert.ToHexStringLower(view.InitialSnapshot.Sha256.Span)+".rws");
            await client.DownloadPreparedInitial(request,view,initial,ct);
            var marker=new BattleViewerMarkerV1{Version=1,EndpointId=owner.Endpoint.Id,Battle=scope.Clone(),Home=home,HomeVersion=accepted.Info.Version,
                ProfileDigest=profile.Digest,ManifestDigest=view.ManifestDigest,Stage=BattleViewerStage.Entering};
            var journal=new BattleViewerMarkerStore(MarkerPath(owner.Endpoint,owner.Login.Player.PlayerId));
            var runtime=new BattlePreparedViewer(marker,homeFile);
            await MainThread.Get(()=>
            {
                ct.ThrowIfCancellationRequested();RequireHomeOwner(owner.Connection,owner.Login);
                if(!ReferenceEquals(Current.Game,owner.Game))throw Invalid();
                BattleGameContext.BeginViewer(marker,journal,accepted,homeFile);current=runtime;
                using(var input=new FileStream(initial,FileMode.Open,FileAccess.Read,FileShare.Read))BattleScenario.CheckDocument(XDocument.Load(input),true);
                string name="GuildView_"+scope.BattleId+"_"+manifest.ProposalSequence;
                CopyOnce(initial,GenFilePaths.FilePathForSavedGame(name),view.InitialSnapshot.Sha256.ToByteArray());Reload(name);return true;
            });
            await Wait(()=>
            {
                RequireHomeOwner(owner.Connection,owner.Login);
                if(Current.ProgramState!=ProgramState.Playing || Current.Game is null || ReferenceEquals(Current.Game,owner.Game) || !Current.Game.PlayerHasControl || LongEventHandler.AnyEventNowOrWaiting)return false;
                if(!BattleGameContext.BindLoadedBattle(Current.Game) || Find.TickManager.TicksGame!=manifest.InitialTick
                    || Find.CurrentMap.mapPawns.AllPawnsSpawned.Count!=8)throw Invalid();
                foreach(var entity in manifest.Entities)
                    if(Find.CurrentMap.mapPawns.AllPawnsSpawned.Count(p=>p.ThingID==entity.EntityId)!=1)throw Invalid();
                runtime.running=new(marker,manifest,view.Side,Find.CurrentMap,view.StartCommitted);
                if (view.StartCommitted) resumed = runtime.running;
                return true;
            },120,ct);
        }
        catch(Exception error)
        {
            await MainThread.Get(()=>{if(current is not null)current.failure=error.Message;return true;});throw;
        }
        finally { await MainThread.Get(() => { Busy = false; resumed?.Begin(false); return true; }); }
    }
    public static async Task ReturnHome(CancellationToken ct)
    {
        var owner=await MainThread.Get(()=>
        {
            ct.ThrowIfCancellationRequested();var held=current??throw Invalid();if(Busy || !Viewing)throw Invalid();
            var connection=RimlinkSession.Connection??throw Invalid();var login=connection.Login??throw Invalid();
            var endpoint=RimlinkSession.Endpoint??throw Invalid();RequireHomeOwner(connection,login);
            var scope=new GuildColonyScope{WorldInstanceId=RimlinkSession.World?.WorldInstanceId??"",RestoreBoundary=login.OutpostRestoreBoundary,
                PlayerId=login.Player.PlayerId,SaveGeneration=login.SaveGeneration,SaveWriteEpoch=login.SaveWriteEpoch};
            held.running?.Stop();
            Busy=true;return(Held:held,Connection:connection,Login:login,Endpoint:endpoint,Scope:scope,Game:Current.Game);
        });
        var held=owner.Held;
        try
        {
            await RequireCanonicalHome();
            string name="GuildHome_"+held.marker.Home.SnapshotId;
            await MainThread.Get(()=>
            {
                ct.ThrowIfCancellationRequested();RequireHomeOwner(owner.Connection,owner.Login);
                if(!ReferenceEquals(Current.Game,owner.Game))throw Invalid();
                BattleGameContext.BeginHomeReturn();
                CopyOnce(held.homeFile,GenFilePaths.FilePathForSavedGame(name),held.marker.Home.RawSha256.ToByteArray());Reload(name);return true;
            });
            await Wait(()=>
            {
                RequireHomeOwner(owner.Connection,owner.Login);
                return Current.ProgramState==ProgramState.Playing && Current.Game is not null && !ReferenceEquals(Current.Game,owner.Game) && Current.Game.PlayerHasControl && !LongEventHandler.AnyEventNowOrWaiting;
            },120,ct);
            await RequireCanonicalHome();
            await MainThread.Get(()=>
            {
                ct.ThrowIfCancellationRequested();RequireHomeOwner(owner.Connection,owner.Login);
                BattleGameContext.CompleteViewerHome(Current.Game,held.marker.Home,held.marker.HomeVersion,GenFilePaths.FilePathForSavedGame(name));
                current=null;RimlinkSession.RecoveryNeeded=true;return true;
            });
        }
        catch(Exception error)
        {await MainThread.Get(()=>{held.failure=error.Message;return true;});throw;}
        finally{await MainThread.Get(()=>{Busy=false;return true;});}

        async Task RequireCanonicalHome()
        {
            var versions=await owner.Connection.ForAuthentication(owner.Login,()=>owner.Connection.Saves.ListAsync(new Empty(),
                deadline:DateTime.UtcNow.AddSeconds(10),cancellationToken:ct).ResponseAsync);
            BattleCanonicalHome.RequireRetained(held.marker,owner.Endpoint.Id,held.homeFile,versions.Versions.SingleOrDefault(v=>v.IsCurrent),owner.Scope);
        }
    }
    public static void BeginControl(bool attackerReady) => (current?.running ?? throw Invalid()).Begin(attackerReady);
    public static Task<BattleSubmitCommandReply> Order(BattleCommandV1 command) => (current?.running ?? throw Invalid()).Order(command);
    public static BattleRunningViewV1? RunningView() => current?.running?.ReadView();
    public static BattleAppliedReceipt? CompletedResult() => current?.running?.ReadResult();
    internal static void DrawZone() => current?.running?.DrawZone();
    internal static void HandleMapClick() => current?.running?.HandleMapClick();

    public static void Draw()
    {
        if(!Viewing || Current.ProgramState!=ProgramState.Playing)return;
        Text.Font=GameFont.Small;float x=Verse.UI.screenWidth-250f;
        Widgets.Label(new Rect(x,60,240,28),(current!.running?.Running==true?"Rimlink.GuildBattle.LiveView":"Rimlink.GuildBattle.PreparedView").Translate());
        if(current!.failure is not null)Widgets.Label(new Rect(x,88,240,52),"Rimlink.GuildBattle.RecoveryRequired".Translate());
        if(!Busy && Widgets.ButtonText(new Rect(x,145,240,28),"Rimlink.GuildBattle.ReturnHome".Translate()))
            _=Task.Run(async()=>{try{await ReturnHome(RimlinkSession.RecoveryToken);}catch(Exception error){RimlinkLog.Instance.Warn("battle home return held: "+error.Message);}});
        current!.running?.Draw(x);
    }
    private static async Task Wait(Func<bool> ready,int seconds,CancellationToken ct)
    {
        var watch=Stopwatch.StartNew();
        while(watch.Elapsed<TimeSpan.FromSeconds(seconds))
        {
            ct.ThrowIfCancellationRequested();if(await MainThread.Get(()=>{ct.ThrowIfCancellationRequested();return ready();}))return;
            await Task.Delay(50,ct);
        }
        throw new TimeoutException("battle viewer load or home recovery did not finish");
    }
    private static void Reload(string name)
    {GlobalTextureAtlasManager.FreeAllRuntimeAtlases();PortraitsCache.Clear();using(BattleGameContext.AllowLoad(name))GameDataSaveLoader.LoadGame(name);}
    private static void CopyOnce(string source,string target,byte[] hash)
    {
        NoLinks(source);NoLinks(target);NoLinks(target+".part");
        using var input=new FileStream(source,FileMode.Open,FileAccess.Read,FileShare.Read);
        if(input.Length is <1 or >67108864 || hash.Length!=32 || !SHA256.HashData(input).AsSpan().SequenceEqual(hash))throw Invalid();
        if(File.Exists(target))
        {using var existing=new FileStream(target,FileMode.Open,FileAccess.Read,FileShare.Read);if(existing.Length!=input.Length || !SHA256.HashData(existing).AsSpan().SequenceEqual(hash))throw Invalid();return;}
        if(File.Exists(target+".part"))throw Invalid();Directory.CreateDirectory(Path.GetDirectoryName(target)!);input.Position=0;
        using(var output=new FileStream(target+".part",FileMode.CreateNew,FileAccess.Write,FileShare.None)){input.CopyTo(output);output.Flush(true);}
        File.Move(target+".part",target,false);
    }
    private static void RequireHomeOwner(RimlinkConnection connection,LoginReply login)
    {
        if(!ReferenceEquals(RimlinkSession.Connection,connection) || !ReferenceEquals(connection.Login,login) || connection.State!=ConnectionState.Connected
            || login.GuildVersion!=GuildResourceContracts.CurrentVersion || RimlinkSession.MyPlayerId!=login.Player.PlayerId)throw Invalid();
    }
    private static void NoLinks(string path)
    {
        for(string? current=Path.GetFullPath(path);current is not null;current=Path.GetDirectoryName(current))
            if((File.Exists(current)||Directory.Exists(current)) && (File.GetAttributes(current)&FileAttributes.ReparsePoint)!=0)throw Invalid();
    }
    private static InvalidDataException Invalid()=>new("battle-viewer-home-recovery-required");
}
