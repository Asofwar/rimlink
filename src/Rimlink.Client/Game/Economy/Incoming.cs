using Grpc.Core;
using RimWorld;
using Rimlink.Client.Economy;
using Rimlink.Client.UI;
using Rimlink.Protocol.V1;
using Verse;

namespace Rimlink.Client.Game.Economy;

/// <summary>
/// Parcels become drop pods on the player's home map. The mailbox list carries descriptors only; the blobs of one
/// parcel are fetched with <c>Mailbox.Get</c> right before the drop. A parcel is confirmed (<c>Mailbox.Deliver</c>) only
/// after its pod exists; the confirmation is journaled in the outbox first, so a lost answer is replayed, never
/// guessed. A parcel that cannot be unpacked stays in the mailbox. Main thread only.
/// </summary>
public static class Incoming
{
    private static readonly HashSet<long> spawnedThisSession = [];
    private static readonly HashSet<long> fetching = [];
    private static DateTime lastAttempt = DateTime.MinValue;

    public static void Reset()
    {
        spawnedThisSession.Clear();
        fetching.Clear();
        Outposts.OutpostIncoming.Reset();
    }

    /// <summary>Called every frame by the game component; throttled, and only while on the colony map.</summary>
    public static void Tick()
    {
        if (SaveSync.WritesBlocked) return;
        if (DateTime.UtcNow - lastAttempt < TimeSpan.FromSeconds(5)) return;
        if (Visits.VisitSession.InVisit) { lastAttempt = DateTime.UtcNow; return; } // the loaded game is the host's colony, not ours
        if (RimlinkSession.State.Economy.Pending.Count > 0) DeliverPending();
        else lastAttempt = DateTime.UtcNow;
        if (Current.ProgramState == ProgramState.Playing && Find.CurrentMap is { IsPlayerHome: true } map) Outgoing.ReturnRefused(map);
    }

    /// <summary>The parcel dropped in this session (a storage fee refers to it, not to a lot).</summary>
    public static bool DeliveredThisSession(long parcelId) => spawnedThisSession.Contains(parcelId);

    public static void DeliverPending()
    {
        lastAttempt = DateTime.UtcNow;
        if (!RimlinkSession.Active || Visits.VisitSession.InVisit || RimlinkSession.Economy is not { } economy || Current.ProgramState != ProgramState.Playing) return;
        var map = Find.CurrentMap;
        if (map is null || !map.IsPlayerHome) return;
        var outbox = RimlinkSession.Outbox;
        var connection = RimlinkSession.Connection;
        var game = Current.Game;

        foreach (var parcel in RimlinkSession.State.Economy.Pending.ToList())
        {
            long id = parcel.Id;
            var component = Current.Game.GetComponent<RimlinkGameComponent>();
            if (component.ParcelReceipts.Contains(id))
            {
                PrepareConfirmation(id, outbox);
                RimlinkSession.State.Economy.MarkDelivered(id);
                continue;
            }
            // Once per session: a redelivery flag from the server refers to saves, and this session has not saved the drop yet.
            if (spawnedThisSession.Contains(id) || fetching.Contains(id)) continue;
            if (parcel.AwaitingDecision) continue; // a gift waiting for accept or refuse (the letter, the mailbox tab)
            fetching.Add(id);
            _ = Task.Run(async () =>
            {
                try
                {
                    var full = await economy.Get(id, CancellationToken.None);
                    MainThread.Post(() => { if (RimlinkSession.Connection == connection && Current.Game == game) Drop(full, map, economy, outbox); });
                }
                catch (RpcException ex) when (ex.StatusCode == StatusCode.NotFound)
                {
                    RimlinkLog.Instance.Warn($"parcel {id} is no longer in the mailbox");
                    MainThread.Post(() => { if (RimlinkSession.Connection == connection && Current.Game == game)
                        { fetching.Remove(id); RimlinkSession.State.Economy.MarkDelivered(id); } });
                }
                catch (Exception ex)
                {
                    RimlinkLog.Instance.Warn($"parcel {id} could not be fetched, retrying: {ex.Message}");
                    MainThread.Post(() => { if (RimlinkSession.Connection == connection && Current.Game == game) fetching.Remove(id); });
                }
            });
        }
    }

    private static void Drop(Parcel parcel, Map map, EconomyClient economy, TransferOutbox? outbox)
    {
        long id = parcel.Id;
        fetching.Remove(id);
        if (!RimlinkSession.Active || Current.ProgramState != ProgramState.Playing || !Find.Maps.Contains(map) || spawnedThisSession.Contains(id)) return;
        if (Outposts.OutpostIncoming.Protected(parcel)) { Outposts.OutpostIncoming.Receive(parcel, map); return; }
        var component = Current.Game.GetComponent<RimlinkGameComponent>();
        if (component.ParcelReceipts.Contains(id)) { PrepareConfirmation(id, outbox); RimlinkSession.State.Economy.MarkDelivered(id); return; }
        List<Thing> things;
        try
        {
            things = Unpack(parcel);
        }
        catch (Exception ex)
        {
            RimlinkLog.Instance.Warn($"parcel {id} could not be unpacked: {ex.Message}");
            Messages.Message("Rimlink.Economy.ParcelFailed".Translate(ex.Message), MessageTypeDefOf.NegativeEvent, historical: false);
            spawnedThisSession.Add(id); // no retry loop every 5 s; the next login tries again
            return;
        }

        var center = DropCenter(map);
        // Prepared is durable before the map changes; it cannot acknowledge a pod not yet saved.
        PrepareConfirmation(id, outbox);
        try { DropPodUtility.DropThingsNear(center, map, things, openDelay: 110, canInstaDropDuringInit: false, leaveSlag: false, canRoofPunch: true, forbid: false); }
        catch { SaveSync.BlockWrites(); RimlinkGameComponent.AbortToMenu(); throw; }
        component.ParcelReceipts.Add(id);
        spawnedThisSession.Add(id);
        Messages.Message("Rimlink.Economy.ParcelDropped".Translate(parcel.From?.Name ?? ("Rimlink.Economy.Kind." + parcel.Kind).Translate().ToString()), new LookTargets(center, map), MessageTypeDefOf.PositiveEvent, historical: false);
        RimlinkLog.Instance.Info($"parcel {id} dropped: {things.Count} things");

        RimlinkSession.State.Economy.MarkDelivered(id);
        SaveSync.TriggerSave("delivery");
    }

    private static void PrepareConfirmation(long id, TransferOutbox? outbox)
    {
        if (outbox is null) throw new IOException("delivery has no recovery journal");
        if (!outbox.Contains(OutboxKind.Deliver, message => ((DeliverRequest)message).ParcelId == id))
            outbox.Prepare(new DeliverRequest { ParcelId = id, GameTick = Find.TickManager.TicksGame });
        RimlinkSession.RecoveryNeeded = true;
    }

    private static List<Thing> Unpack(Parcel parcel)
    {
        // A def from a mod this game lacks: named here, before a Scribe load that would only fail on an anonymous blob.
        if (MissingDefs(parcel.Items, parcel.Pawns) is { } missing) throw new InvalidDataException(missing);
        var things = new List<Thing>();
        // Fresh IDs: these objects come from another game whose numbering overlaps ours.
        foreach (var item in parcel.Items.Items) things.Add(ThingBlobs.DeserializeItem(item, newIds: true));
        foreach (var pawn in parcel.Pawns.Pawns) things.Add(ThingBlobs.DeserializePawn(pawn, newIds: true));
        if (parcel.Kind == ParcelKind.ParcelWithdraw && parcel.Silver > 0) things.AddRange(SilverStacks(parcel.Silver));
        // Pawns of this colony back home get their relations back, together, before they are put in the pod.
        return RelationLedger.RejoinAll(things);
    }

    /// <summary>The player-facing reason when blobs declare defs this game does not have (a mod is missing here), else null.</summary>
    public static string? MissingDefs(ItemList? items, PawnList? pawns)
        => MissingDefs(items, pawns, null);
    internal static string? MissingDefs(ItemList? items, PawnList? pawns, Func<string, bool, bool>? availability)
    {
        bool ThingExists(string name)
        {
            bool actual = DefDatabase<ThingDef>.GetNamedSilentFail(name) is not null;
            return actual && (availability?.Invoke("thing:" + name, actual) ?? true);
        }
        bool KindExists(string name)
        {
            bool actual = DefDatabase<PawnKindDef>.GetNamedSilentFail(name) is not null;
            return actual && (availability?.Invoke("kind:" + name, actual) ?? true);
        }
        var missing = BlobDefs.Missing(items, pawns, ThingExists, KindExists);
        return missing.Count == 0 ? null : "Rimlink.Economy.MissingDefs".Translate(string.Join(", ", missing));
    }

    public static IEnumerable<Thing> SilverStacks(long amount)
    {
        long left = amount;
        while (left > 0)
        {
            var silver = ThingMaker.MakeThing(ThingDefOf.Silver);
            silver.stackCount = (int)Math.Min(left, ThingDefOf.Silver.stackLimit);
            left -= silver.stackCount;
            yield return silver;
        }
    }

    /// <summary>The player's delivery spot, else the first trade beacon, else the vanilla trade drop spot.</summary>
    public static IntVec3 DropCenter(Map map)
    {
        var spot = map.listerThings.ThingsOfDef(DeliverySpotDef).FirstOrDefault();
        if (spot is not null) return spot.Position;
        var beacon = map.listerBuildings.AllBuildingsColonistOfDef(ThingDefOf.OrbitalTradeBeacon).FirstOrDefault();
        return beacon is not null ? beacon.Position : DropCellFinder.TradeDropSpot(map);
    }

    private static ThingDef? deliverySpotDef;
    private static ThingDef DeliverySpotDef => deliverySpotDef ??= DefDatabase<ThingDef>.GetNamed("Rimlink_DeliverySpot");
}
