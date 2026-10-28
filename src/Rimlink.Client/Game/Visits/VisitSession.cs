using RimWorld;
using RimWorld.Planet;
using Rimlink.Client.Economy;
using Rimlink.Client.Game.Economy;
using Rimlink.Client.UI;
using Rimlink.Protocol.V1;
using Verse;

namespace Rimlink.Client.Game.Visits;

/// <summary>What a visit needs between the two game loads: the ticket, the host, and the guests as blobs.</summary>
public sealed record VisitPending(long VisitId, long HostId, string HostName, List<PawnBlob> Guests, ShopList Shop);

/// <summary>
/// A visit is a temporary game: the visitor's own colony is saved and uploaded, the host's save is downloaded and loaded
/// with the host's colony turned into a friendly faction and copies of the guests walking in. Nothing goes home from
/// the copy; purchases arrive as parcels after the visit. Main thread entry points.
/// </summary>
public static class VisitSession
{
    public static VisitPending? Pending { get; private set; }
    public static bool Active { get; private set; }
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
        if (!RimlinkSession.Active || RimlinkSession.Visits is not { } visits || RimlinkSession.Shop is not { } shop || Active || Pending is not null) return;
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
                MainThread.Post(() => { wait.Close(); Messages.Message("Rimlink.Visit.Failed".Translate(reason), MessageTypeDefOf.RejectInput, historical: false); });
                return;
            }
            MainThread.Post(() => SaveAndLoad(new VisitPending(ticket.VisitId, host.PlayerId, host.Name, blobs, shopList), wait, visits));
        });
    }

    /// <summary>The visitor's own colony goes to disk and to the server first; then the host's save comes down and is loaded.</summary>
    private static void SaveAndLoad(VisitPending pending, Dialog_Wait wait, Rimlink.Client.Visits.VisitClient visits)
    {
        if (!SaveSync.TriggerSave("visit"))
        {
            wait.Close();
            Messages.Message("Rimlink.Economy.SaveBusy".Translate(), MessageTypeDefOf.RejectInput, historical: false);
            _ = visits.End(pending.VisitId, CancellationToken.None);
            return;
        }
        bool uploaded = false;
        LongEventHandler.QueueLongEvent(() => uploaded = SaveSync.WaitForUpload(TimeSpan.FromSeconds(60)), "Rimlink.Flow.Uploading", doAsynchronously: true, null, showExtraUIInfo: false, callback: () =>
        {
            if (!uploaded || SaveSync.LastError is not null)
            {
                wait.Close();
                Messages.Message("Rimlink.Economy.UploadFailed".Translate(SaveSync.LastError ?? "timeout"), MessageTypeDefOf.RejectInput, historical: false);
                _ = visits.End(pending.VisitId, CancellationToken.None);
                return;
            }
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
                    _ = visits.End(pending.VisitId, CancellationToken.None);
                    MainThread.Post(() => { wait.Close(); Messages.Message("Rimlink.Visit.Failed".Translate(reason), MessageTypeDefOf.RejectInput, historical: false); });
                    return;
                }
                MainThread.Post(() =>
                {
                    wait.Close();
                    Pending = pending;
                    RimlinkLog.Instance.Info($"visit {pending.VisitId}: loading {pending.HostName}'s colony");
                    GameDataSaveLoader.LoadGame(SaveName(pending.HostId));
                });
            });
        });
    }

    /// <summary>After the host's save finished loading: their colony becomes a friendly faction, the guests walk in.</summary>
    public static void OnGameLoaded()
    {
        if (Pending is not { } pending) return;
        Pending = null;
        try
        {
            VisitLoader.Apply(pending);
            Active = true;
            VisitId = pending.VisitId;
            HostId = pending.HostId;
            HostName = pending.HostName;
            HostShop.Clear();
            foreach (var item in pending.Shop.Items) HostShop[item.ThingId] = item;
            Messages.Message("Rimlink.Visit.Arrived".Translate(pending.HostName), MessageTypeDefOf.PositiveEvent, historical: false);
            RimlinkLog.Instance.Info($"visit {pending.VisitId} at {pending.HostName}: {pending.Guests.Count} guests, {HostShop.Count} things for sale");
        }
        catch (Exception ex)
        {
            Log.Error("[Rimlink] the visit could not start on the loaded colony: " + ex);
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
        if (RimlinkSession.Visits is { } visits) _ = visits.End(visitId, CancellationToken.None);
        try
        {
            string path = GenFilePaths.FilePathForSavedGame(SaveName(hostId));
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            RimlinkLog.Instance.Warn("could not delete the visit file: " + ex.Message);
        }
        if (!RimlinkSession.Active) { GenScene.GoToMainMenu(); return; }
        RimlinkLog.Instance.Info("visit over, loading the own colony");
        GameDataSaveLoader.LoadGame(SaveSync.SaveFileName);
    }

    /// <summary>The session ended (lost connection, menu): a visit cannot continue without its ticket.</summary>
    public static void Reset()
    {
        Active = false;
        Pending = null;
        HostShop.Clear();
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
        host.SetRelationDirect(player, FactionRelationKind.Ally, canSendHostilityLetter: false);

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
            var guest = ThingBlobs.DeserializePawn(blob, newIds: true);
            guest.SetFaction(player);
            GenSpawn.Spawn(guest, CellFinder.RandomClosewalkCellNear(entry, target, 4), target);
        }
        Find.TickManager.Pause();
        CameraJumper.TryJump(entry, target);
    }
}
