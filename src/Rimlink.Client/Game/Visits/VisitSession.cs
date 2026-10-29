using RimWorld;
using RimWorld.Planet;
using Rimlink.Client.Economy;
using Rimlink.Client.Game.Economy;
using Rimlink.Client.UI;
using Rimlink.Protocol.V1;
using Verse;

namespace Rimlink.Client.Game.Visits;

/// <summary>What a visit needs between the two game loads: the ticket, the host, and the guests as blobs.</summary>
public sealed record VisitPending(long VisitId, long HostId, string HostName, List<PawnBlob> Guests, ShopList Shop, LiveSession? Live = null);

/// <summary>
/// A visit is a temporary game: the visitor's own colony is saved and uploaded, the host's save is downloaded and loaded
/// with the host's colony turned into a friendly faction and copies of the guests walking in. Nothing goes home from
/// the copy; purchases arrive as parcels after the visit. Main thread entry points.
/// </summary>
public static class VisitSession
{
    public static VisitPending? Pending { get; private set; }
    public static bool Active { get; private set; }
    /// <summary>
    /// True from the moment the host's save starts loading until the visit is over: the loaded game is not this
    /// player's colony, so nothing that acts on "the home map" (deliveries, raids, shop removals, saves) may run.
    /// </summary>
    public static bool InVisit => Active || Pending is not null;
    /// <summary>The visit ticket was asked for and the answer is not in yet: no second visit or live proposal meanwhile.</summary>
    public static bool Starting { get; private set; }
    public static long VisitId { get; private set; }
    public static long HostId { get; private set; }
    public static string HostName { get; private set; } = "";
    /// <summary>The host's shop at visit start, by thing id; counts shrink with purchases.</summary>
    public static Dictionary<long, ShopItem> HostShop { get; } = [];

    public static string SaveName(long hostId) => $"Rimlink_visit_{hostId}";
    public static bool IsVisitSave(string saveName) => saveName.StartsWith("Rimlink_visit_", StringComparison.Ordinal);

    /// <summary>Caravan (or pod) guests on the host's tile: copies of them go visiting; the originals wait where they are.</summary>
    public static void Begin(PlayerRef host, IReadOnlyList<Pawn> guests)
    {
        if (!RimlinkSession.Active || RimlinkSession.Visits is not { } visits || RimlinkSession.Shop is not { } shop || Active || Pending is not null || Starting) return;
        if (guests.Count == 0) { Messages.Message("Rimlink.Visit.NoPawns".Translate(), MessageTypeDefOf.RejectInput, historical: false); return; }
        List<PawnBlob> blobs;
        try
        {
            blobs = guests.Select(ThingBlobs.SerializePawn).ToList();
        }
        catch (Exception ex)
        {
            Log.Error("[Rimlink] could not serialise the guests: " + ex);
            Messages.Message("Rimlink.Visit.Failed".Translate(ex.Message), MessageTypeDefOf.RejectInput, historical: false);
            return;
        }
        long tick = Find.TickManager.TicksGame;
        var wait = new Dialog_Wait("Rimlink.Visit.Preparing".Translate(host.Name), new CancellationTokenSource());
        Find.WindowStack.Add(wait);
        Starting = true;
        _ = Task.Run(async () =>
        {
            VisitTicket ticket;
            ShopList shopList;
            try
            {
                ticket = await visits.Begin(host.PlayerId, tick, CancellationToken.None);
                shopList = await shop.Of(host.PlayerId, CancellationToken.None);
            }
            catch (Exception ex)
            {
                string reason = ex is Grpc.Core.RpcException rpc ? EconomyClient.MessageOf(rpc) : ex.Message;
                MainThread.Post(() => { Starting = false; wait.Close(); Messages.Message("Rimlink.Visit.Failed".Translate(reason), MessageTypeDefOf.RejectInput, historical: false); });
                return;
            }
            MainThread.Post(() => { Starting = false; SaveAndLoad(new VisitPending(ticket.VisitId, host.PlayerId, host.Name, blobs, shopList), wait, visits); });
        });
    }

    /// <summary>A live session (3b): the host's snapshot is loaded as a visit copy in mirror mode; the session is the ticket. Also used for a resync from inside the copy.</summary>
    public static void BeginLive(LiveSession session, Action onFailure)
    {
        if (!RimlinkSession.Active || RimlinkSession.Visits is not { } visits || RimlinkSession.Shop is not { } shop || Pending is not null || session.Host is null) { onFailure(); return; }
        var host = session.Host;
        var wait = new Dialog_Wait("Rimlink.Visit.Preparing".Translate(host.Name), new CancellationTokenSource());
        Find.WindowStack.Add(wait);
        _ = Task.Run(async () =>
        {
            ShopList list;
            try { list = await shop.Of(host.PlayerId, CancellationToken.None); }
            catch (Exception ex) { RimlinkLog.Instance.Warn("live: the host's shop list was not fetched: " + ex.Message); list = new ShopList(); }
            MainThread.Post(() => SaveAndLoad(new VisitPending(0, host.PlayerId, host.Name, [], list, session), wait, visits, onFailure));
        });
    }

    private static void Fail(VisitPending pending, Rimlink.Client.Visits.VisitClient visits, string reason, Action? onFailure)
    {
        if (pending.VisitId != 0) _ = visits.End(pending.VisitId, CancellationToken.None);
        Messages.Message("Rimlink.Visit.Failed".Translate(reason), MessageTypeDefOf.RejectInput, historical: false);
        onFailure?.Invoke();
        if (pending.Live is not null) Live.LiveGuest.Abort(reason);
    }

    /// <summary>The visitor's own colony goes to disk and to the server first; then the host's save comes down and is loaded.</summary>
    private static void SaveAndLoad(VisitPending pending, Dialog_Wait wait, Rimlink.Client.Visits.VisitClient visits, Action? onFailure = null)
    {
        if (pending.Live is not null && Active)
        {
            // A resync from inside the host's copy: nothing of ours to save, straight to the new snapshot (Active stays until it loads).
            Download(pending, wait, visits, onFailure);
            return;
        }
        if (!SaveSync.TriggerSave("visit"))
        {
            wait.Close();
            Fail(pending, visits, "Rimlink.Economy.SaveBusy".Translate(), onFailure);
            return;
        }
        bool uploaded = false;
        LongEventHandler.QueueLongEvent(() => uploaded = SaveSync.WaitForUpload(TimeSpan.FromSeconds(60)), "Rimlink.Flow.Uploading", doAsynchronously: true, null, showExtraUIInfo: false, callback: () =>
        {
            if (!uploaded || SaveSync.LastError is not null)
            {
                wait.Close();
                Fail(pending, visits, "Rimlink.Economy.UploadFailed".Translate(SaveSync.LastError ?? "timeout"), onFailure);
                return;
            }
            Download(pending, wait, visits, onFailure);
        });
    }

    private static void Download(VisitPending pending, Dialog_Wait wait, Rimlink.Client.Visits.VisitClient visits, Action? onFailure)
    {
        wait.Text = "Rimlink.Visit.Downloading".Translate(pending.HostName);
        string path = GenFilePaths.FilePathForSavedGame(SaveName(pending.HostId));
        _ = Task.Run(async () =>
        {
            try
            {
                if (await visits.DownloadOf(pending.HostId, path, CancellationToken.None) is null) throw new InvalidOperationException("the host has no save");
            }
            catch (Exception ex)
            {
                string reason = ex is Grpc.Core.RpcException rpc ? EconomyClient.MessageOf(rpc) : ex.Message;
                MainThread.Post(() => { wait.Close(); Fail(pending, visits, reason, onFailure); });
                return;
            }
            MainThread.Post(() =>
            {
                wait.Close();
                Active = false;
                Pending = pending;
                RimlinkLog.Instance.Info($"visit {pending.VisitId}: loading {pending.HostName}'s colony{(pending.Live is { } live ? $" (live session {live.Id}, snapshot v{live.SnapshotVersion})" : "")}");
                ForgetPawnTextures();
                GameDataSaveLoader.LoadGame(SaveName(pending.HostId));
            });
        });
    }

    /// <summary>After the host's save finished loading: their colony becomes another faction (friendly, hostile in a live raid), the guests walk in.</summary>
    public static void OnGameLoaded()
    {
        if (Pending is not { } pending) return;
        Pending = null;
        try
        {
            if (Find.Maps.Count == 0) throw new InvalidOperationException("the host's save loaded without a map");
            VisitLoader.Apply(pending);
            Active = true;
            VisitId = pending.VisitId;
            HostId = pending.HostId;
            HostName = pending.HostName;
            HostShop.Clear();
            foreach (var item in pending.Shop.Items) HostShop[item.ThingId] = item;
            if (pending.Live is { } live) { Live.LiveGuest.OnMirrorLoaded(live); return; }
            Messages.Message("Rimlink.Visit.Arrived".Translate(pending.HostName), MessageTypeDefOf.PositiveEvent, historical: false);
            RimlinkLog.Instance.Info($"visit {pending.VisitId} at {pending.HostName}: {pending.Guests.Count} guests, {HostShop.Count} things for sale");
        }
        catch (Exception ex)
        {
            Log.Error("[Rimlink] the visit could not start on the loaded colony: " + ex);
            if (pending.Live is not null) { Active = true; Live.LiveGuest.Abort(ex.Message); return; }
            Abort(pending.VisitId, pending.HostId, ex.Message);
        }
    }

    /// <summary>The host's copy could not be used: back to the visitor's own colony.</summary>
    public static void Abort(long visitId, long hostId, string reason)
    {
        Messages.Message("Rimlink.Visit.Failed".Translate(reason), MessageTypeDefOf.NegativeEvent, historical: false);
        Leave(visitId, hostId);
    }

    /// <summary>"End visit": the copy is thrown away and the visitor's own save comes back.</summary>
    public static void End()
    {
        if (!Active) return;
        Leave(VisitId, HostId);
    }

    private static void Leave(long visitId, long hostId)
    {
        Active = false;
        HostShop.Clear();
        if (visitId != 0 && RimlinkSession.Visits is { } visits) _ = visits.End(visitId, CancellationToken.None);
        DeleteVisitFile(hostId);
        if (!RimlinkSession.Active) { GenScene.GoToMainMenu(); return; }
        RimlinkLog.Instance.Info("visit over, loading the own colony");
        ForgetPawnTextures();
        GameDataSaveLoader.LoadGame(SaveSync.SaveFileName);
    }

    /// <summary>
    /// The pawn texture atlas is global and keyed by pawn id; a pawn that left this game (sent away, destroyed) keeps
    /// a stale key that throws on every frame of the next game. Freed before any switch of games.
    /// </summary>
    private static void ForgetPawnTextures()
    {
        try
        {
            GlobalTextureAtlasManager.FreeAllRuntimeAtlases();
            PortraitsCache.Clear();
        }
        catch (Exception ex)
        {
            RimlinkLog.Instance.Warn("pawn textures not freed: " + ex.Message);
        }
    }

    private static void DeleteVisitFile(long hostId)
    {
        try
        {
            string path = GenFilePaths.FilePathForSavedGame(SaveName(hostId));
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            RimlinkLog.Instance.Warn("could not delete the visit file: " + ex.Message);
        }
    }

    /// <summary>
    /// The session is ending (lost connection, menu, a load error on the host's copy): a visit cannot continue without
    /// its ticket. The ticket is closed on a best-effort basis while the connection still exists (the server closes a
    /// forgotten one after Raids:VisitHours anyway) and the host's copy is deleted; the caller takes the game to the menu.
    /// </summary>
    /// <summary>Ends the visit with the session; returns the ticket's End call (or null), which the caller lets finish before disconnecting.</summary>
    public static Task? Reset()
    {
        long visitId = Active ? VisitId : Pending?.VisitId ?? 0;
        long hostId = Active ? HostId : Pending?.HostId ?? 0;
        Active = false;
        Pending = null;
        Starting = false;
        HostShop.Clear();
        if (hostId != 0) DeleteVisitFile(hostId);
        if (visitId == 0) return null;
        RimlinkLog.Instance.Info($"visit {visitId} ends with the session");
        return RimlinkSession.Visits is { } visits ? visits.End(visitId, CancellationToken.None) : null;
    }
}

/// <summary>Turns the loaded host save into a place to visit.</summary>
public static class VisitLoader
{
    public static void Apply(VisitPending pending)
    {
        var player = Faction.OfPlayer;
        var host = FactionGenerator.NewGeneratedFaction(new FactionGeneratorParms(FactionDefOf.OutlanderCivil, default, hidden: false));
        host.Name = pending.HostName;
        Find.FactionManager.Add(host);
        if (pending.Live?.Kind == LiveKind.LiveRaid)
        {
            // A live raid's mirror: the host's colony is the enemy. A drafted pawn's right-click offers attacks on hostile targets
            // only, so a friendly host would leave the guest nothing but "go here".
            host.RelationWith(player).baseGoodwill = -100;
            player.RelationWith(host).baseGoodwill = -100;
            host.Notify_GoodwillSituationsChanged(player, canSendHostilityLetter: false, null, null);
        }
        else host.TryAffectGoodwillWith(player, 100, canSendMessage: false, canSendHostilityLetter: false);

        foreach (var map in Find.Maps)
        {
            foreach (var pawn in map.mapPawns.AllPawns.ToList())
            {
                if (pawn.Faction == player && !pawn.IsPrisoner) pawn.SetFaction(host);
            }
            foreach (var building in map.listerBuildings.allBuildingsColonist.ToList()) building.SetFaction(host);
            foreach (var thing in map.listerThings.AllThings.ToList())
            {
                if (thing is not Building && thing.Faction == player && thing.def.CanHaveFaction) thing.SetFaction(host);
            }
        }
        foreach (var settlement in Find.WorldObjects.Settlements.Where(s => s.Faction == player).ToList()) settlement.SetFaction(host);

        var target = Find.CurrentMap ?? Find.Maps.First();
        if (!RCellFinder.TryFindRandomPawnEntryCell(out var entry, target, CellFinder.EdgeRoadChance_Neutral)) entry = CellFinder.RandomEdgeCell(target);
        foreach (var blob in pending.Guests)
        {
            // The visitor's ideo did not travel: guests take this save's player ideo, the host's.
            var guest = ThingBlobs.DeserializePawn(blob, newIds: true, joins: player);
            GenSpawn.Spawn(guest, CellFinder.RandomClosewalkCellNear(entry, target, 4), target);
        }
        Find.TickManager.Pause();
        CameraJumper.TryJump(entry, target);
    }
}
