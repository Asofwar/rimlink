using Rimlink.Client.GuildCapture;
using Rimlink.Protocol.V1;
using Verse;

namespace Rimlink.Client.Game.GuildCapture;

// Main thread only. The guard begins before LoadGame and survives Entry, errors and session replacement.
public static class BattleGameContext
{
    private static BattleMapGuard guard = new();
    private static BattleViewerMarkerV1? viewer;
    private static BattleViewerMarkerStore? store;
    private static Verse.Game? battleGame;
    private static string? permittedSave,permittedLoad;
    public static bool ApplyingOrders { get; private set; }
    public static bool Active => guard.Active;
    public static BattleMapRole Role => guard.Role;
    public static BattleScopeMessage? Scope => guard.Scope;
    public static bool IsBattleGame => Active && ReferenceEquals(Current.Game,battleGame);

    public static void BeginViewer(BattleViewerMarkerV1 marker,BattleViewerMarkerStore journal,SaveSync.UploadResult accepted,string homeFile)
    {
        RequireIdleViewerHome(marker.Home.Scope);
        if(!accepted.Uploaded || accepted.Info!.Version!=marker.HomeVersion
            || !BattleCanonicalHome.ReadAccepted(homeFile,accepted.Info,marker.Home.Scope).Equals(marker.Home))
            throw new InvalidDataException("battle handoff requires the accepted canonical file, not the last local save");
        var home = Current.Game.GetComponent<RimlinkGameComponent>();
        if (home.SnapshotId != marker.Home.SnapshotId || !Equals(home.OutpostRecovery.Metadata.GuildMetadata?.Scope,marker.Home.Scope))
            throw new InvalidDataException("battle handoff home proof differs from the loaded colony");
        journal.Save(marker); AttachViewer(marker,journal);
    }
    internal static void RecoverViewerAtEntry(BattleViewerMarkerV1 marker,BattleViewerMarkerStore journal)
    {
        if(Active || !AtEntry || !RimlinkSession.Active || !Equals(journal.Read(),marker))
            throw new InvalidDataException("battle viewer recovery requires its retained marker at Entry");
        AttachViewer(marker,journal);
    }
    private static void AttachViewer(BattleViewerMarkerV1 marker,BattleViewerMarkerStore journal)
    {
        var next=new BattleMapGuard();next.BeginViewer(marker.Battle,marker.Home,marker.HomeVersion);
        viewer=marker.Clone();store=journal;guard=next;
    }
    internal static bool ReleaseViewerAtEntry()
    {
        if(Role!=BattleMapRole.Viewer || !AtEntry || RimlinkSession.Connection is not null || viewer is null)return false;
        try { if(!Equals(store?.Read(),viewer))return false; }
        catch(Exception error)when(error is IOException or UnauthorizedAccessException or InvalidDataException){return false;}
        guard=new();viewer=null;store=null;battleGame=null;return true;
    }
    private static bool AtEntry=>Current.ProgramState==ProgramState.Entry && Current.Game is null && !LongEventHandler.AnyEventNowOrWaiting;
    public static void RequireIdleViewerHome(GuildColonyScope homeScope)
    {
        Rimlink.Protocol.GuildResourceContracts.ValidateScope(homeScope);
        if (Active) throw new InvalidOperationException("reconcile the existing battle context first");
        if (!RimlinkSession.Active || RimlinkSession.Login is not { GuildVersion: 1 } login
            || Current.ProgramState != ProgramState.Playing || Current.Game is null || !Current.Game.PlayerHasControl || !Outposts.PawnCustodyRegistry.IsOwnGame
            || Visits.VisitSession.InVisit || Visits.VisitSession.Starting || Live.LiveHost.Active || Live.LiveHost.Starting
            || Live.LiveGuest.Session is not null || Live.MirrorMode.Active || SaveSync.IsSaving || SaveSync.UploadInFlight || SaveSync.WritesBlocked
            || GuildIncoming.Busy || Outposts.OutpostIncoming.Busy || Outposts.OutpostOutgoing.Busy || Outposts.OutpostSync.Busy
            || RimlinkSession.RecoveryNeeded || Economy.Outgoing.UnderThreat(Find.AnyPlayerHomeMap) || RimlinkSession.Outbox?.HasUnresolved != false
            || RimlinkSession.World?.WorldInstanceId!=homeScope.WorldInstanceId || login.OutpostRestoreBoundary!=homeScope.RestoreBoundary
            || homeScope.PlayerId != login.Player.PlayerId || homeScope.SaveGeneration != login.SaveGeneration
            || homeScope.SaveWriteEpoch != login.SaveWriteEpoch)
            throw new InvalidDataException("battle handoff requires an idle canonical own colony");
    }
    public static void BeginExecutor(BattleScopeMessage scope)
    {
        if (RimlinkSession.Active || RimlinkSession.Connection is not null) throw new InvalidDataException("executor cannot own a player session");
        guard.BeginExecutor(scope);
    }
    public static bool BindLoadedBattle(Verse.Game game)
    {
        if (!Active || !ReferenceEquals(Current.Game,game) || Current.ProgramState != ProgramState.Playing || !game.PlayerHasControl) return false;
        if (Find.Maps.Count != 1 || !Equals(Find.CurrentMap.GetComponent<MapComponent_BattleScope>().Identity,Scope)
            || game.GetComponent<Outposts.PawnCustodyRegistry>().HasRecords) throw new InvalidDataException("loaded battle map or custody identity differs");
        var component = game.GetComponent<RimlinkGameComponent>();
        if (component.OutpostRecovery.Present || component.SnapshotId.Length != 0 || component.ParcelReceipts.Count != 0
            || component.RemovedOperations.Count != 0 || component.ReturnedOperations.Count != 0) throw new InvalidDataException("battle snapshot contains personal evidence");
        battleGame = game;
        if (viewer is not null) { viewer.Stage = BattleViewerStage.Viewing; store!.Save(viewer); }
        return true;
    }
    public static void BeginHomeReturn()
    {
        if (Role != BattleMapRole.Viewer || viewer is null) throw new InvalidOperationException("no viewer home to restore");
        viewer.Stage = BattleViewerStage.ReturningHome; store!.Save(viewer);
    }
    public static void CompleteViewerHome(Verse.Game game,GuildSnapshotProof proof,int version,string homeFile)
    {
        if (Role != BattleMapRole.Viewer || viewer is null || viewer.Stage != BattleViewerStage.ReturningHome || !ReferenceEquals(Current.Game,game)
            || Current.ProgramState != ProgramState.Playing || !game.PlayerHasControl || Find.Maps.Any(m => m.GetComponent<MapComponent_BattleScope>().Present))
            throw new InvalidDataException("home has not been restored");
        var component = game.GetComponent<RimlinkGameComponent>();
        if (component.SnapshotId != proof.SnapshotId || component.SaveGeneration != proof.Scope.SaveGeneration || component.SaveWriteEpoch != proof.Scope.SaveWriteEpoch
            || component.OutpostLoadFailure is not null || !Equals(component.OutpostRecovery.Metadata.GuildMetadata?.Scope,proof.Scope))
            throw new InvalidDataException("loaded home differs from the pinned canonical proof");
        BattleCanonicalHome.RequireFile(homeFile,proof);
        guard.RestoreViewerHome(proof,version);
        try
        {
            component.FinalizeOwnSessionAfterBattle();
            if (component.OutpostLoadFailure is not null || SaveSync.WritesBlocked) throw new InvalidDataException("restored home requires recovery");
            store!.ClearAfterHome(proof,version);
        }
        catch
        {
            var held = new BattleMapGuard(); held.BeginViewer(viewer.Battle,viewer.Home,viewer.HomeVersion); guard = held;
            SaveSync.BlockWrites(); throw;
        }
        viewer = null; store = null; battleGame = null;
    }
    public static void CompleteExecutor(BattleCleanupReceipt receipt)
    {
        if (Current.ProgramState != ProgramState.Entry || Current.Game is not null || BattleTickController.Current is not null || RimlinkSession.Active || RimlinkSession.Connection is not null)
            throw new InvalidDataException("executor is not quiescent in Entry");
        guard.CompleteExecutor(receipt); battleGame = null;
    }
    public static bool MaySave(string fileName) => !Active || Role == BattleMapRole.Executor && permittedSave == fileName;
    public static bool MayLoad(string fileName) => !Active || permittedLoad == fileName;
    public static IDisposable AllowBattleSave(string fileName)
    {
        if (Role != BattleMapRole.Executor || permittedSave is not null || !Name(fileName)) throw new InvalidOperationException("battle save permit refused");
        permittedSave = fileName; return new Permit(true,fileName);
    }
    public static IDisposable AllowLoad(string fileName)
    {
        if (!Active || permittedLoad is not null || !Name(fileName)) throw new InvalidOperationException("battle load permit refused");
        permittedLoad = fileName; return new Permit(false,fileName);
    }
    internal static IDisposable AllowExecutorOrders()
    {
        if (Role!=BattleMapRole.Executor || ApplyingOrders || RimlinkSession.Connection is not null || RimlinkSession.Active)
            throw new InvalidOperationException("native battle order permit refused");
        ApplyingOrders=true; return new OrderPermit();
    }
    private sealed class OrderPermit : IDisposable
    {
        private bool disposed;
        public void Dispose(){if(disposed)return;disposed=true;ApplyingOrders=false;}
    }
    private static bool Name(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 240 && value.IndexOfAny(['/', '\\', ':']) < 0;
    private sealed class Permit(bool save,string name) : IDisposable
    {
        private bool disposed;
        public void Dispose()
        {
            if (disposed) return; disposed = true;
            if (save && permittedSave == name) permittedSave = null;
            if (!save && permittedLoad == name) permittedLoad = null;
        }
    }
}
