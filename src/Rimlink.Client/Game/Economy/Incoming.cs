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
    }

    /// <summary>Called every frame by the game component; throttled, and only while on the colony map.</summary>
    public static void Tick()
    {
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

        foreach (var parcel in RimlinkSession.State.Economy.Pending.ToList())
        {
            long id = parcel.Id;
            // Once per session: a redelivery flag from the server refers to saves, and this session has not saved the drop yet.
            if (spawnedThisSession.Contains(id) || fetching.Contains(id)) continue;
            if (parcel.AwaitingDecision) continue; // a gift waiting for accept or refuse (the letter, the mailbox tab)
            if (outbox is not null && outbox.Contains(OutboxKind.Deliver, m => ((DeliverRequest)m).ParcelId == id))
            {
                // Dropped in an earlier session; its confirmation is still on its way to the server.
                spawnedThisSession.Add(id);
                continue;
            }
            fetching.Add(id);
            _ = Task.Run(async () =>
            {
                try
                {
                    var full = await economy.Get(id, CancellationToken.None);
                    MainThread.Post(() => Drop(full, map, economy, outbox));
                }
                catch (RpcException ex) when (ex.StatusCode == StatusCode.NotFound)
                {
                    RimlinkLog.Instance.Warn($"parcel {id} is no longer in the mailbox");
                    MainThread.Post(() => { fetching.Remove(id); RimlinkSession.State.Economy.MarkDelivered(id); });
                }
                catch (Exception ex)
                {
                    RimlinkLog.Instance.Warn($"parcel {id} could not be fetched, retrying: {ex.Message}");
                    MainThread.Post(() => fetching.Remove(id));
                }
            });
        }
    }

    private static void Drop(Parcel parcel, Map map, EconomyClient economy, TransferOutbox? outbox)
    {
        long id = parcel.Id;
        fetching.Remove(id);
        if (!RimlinkSession.Active || Current.ProgramState != ProgramState.Playing || !Find.Maps.Contains(map) || spawnedThisSession.Contains(id)) return;
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
        DropPodUtility.DropThingsNear(center, map, things, openDelay: 110, canInstaDropDuringInit: false, leaveSlag: false, canRoofPunch: true, forbid: false);
        spawnedThisSession.Add(id);
        Messages.Message("Rimlink.Economy.ParcelDropped".Translate(parcel.From?.Name ?? ("Rimlink.Economy.Kind." + parcel.Kind).Translate().ToString()), new LookTargets(center, map), MessageTypeDefOf.PositiveEvent, historical: false);
        RimlinkLog.Instance.Info($"parcel {id} dropped: {things.Count} things");

        long tick = Find.TickManager.TicksGame;
        var entry = outbox?.Add(new DeliverRequest { ParcelId = id, GameTick = tick });
        RimlinkSession.State.Economy.MarkDelivered(id);
        _ = Task.Run(async () =>
        {
            try
            {
                await economy.Deliver(id, tick, CancellationToken.None);
                if (entry is not null) outbox!.Remove(entry.Id);
                MainThread.Post(() => RimlinkSession.State.Economy.MarkWealthStale());
            }
            catch (RpcException ex) when (TransferOutbox.IsDefinitive(ex))
            {
                RimlinkLog.Instance.Warn($"delivery of parcel {id} refused ({EconomyClient.MessageOf(ex)}); the pod stays");
                if (entry is not null) outbox!.Remove(entry.Id);
            }
            catch (Exception ex)
            {
                RimlinkLog.Instance.Warn($"delivery of parcel {id} unconfirmed, kept in the outbox: {ex.Message}");
            }
            // Saved either way: the things exist in the colony now; the confirmation follows from the outbox if it did not get through.
            MainThread.Post(() => SaveSync.TriggerSave("delivery"));
        });
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
    {
        var missing = BlobDefs.Missing(items, pawns, d => DefDatabase<ThingDef>.GetNamedSilentFail(d) is not null, k => DefDatabase<PawnKindDef>.GetNamedSilentFail(k) is not null);
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

    public static IntVec3 DropCenter(Map map)
    {
        var beacon = map.listerBuildings.AllBuildingsColonistOfDef(ThingDefOf.OrbitalTradeBeacon).FirstOrDefault();
        return beacon is not null ? beacon.Position : DropCellFinder.TradeDropSpot(map);
    }
}
