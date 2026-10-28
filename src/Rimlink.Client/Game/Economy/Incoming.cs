using RimWorld;
using Rimlink.Client.Economy;
using Rimlink.Client.UI;
using Rimlink.Protocol.V1;
using Verse;

namespace Rimlink.Client.Game.Economy;

/// <summary>
/// Parcels become drop pods on the player's home map. A parcel is confirmed (<c>Mailbox.Deliver</c>) only after its
/// pod exists; a parcel that cannot be unpacked stays in the mailbox. Main thread only.
/// </summary>
public static class Incoming
{
    private static readonly HashSet<long> spawnedThisSession = [];
    private static DateTime lastAttempt = DateTime.MinValue;

    public static void Reset() => spawnedThisSession.Clear();

    /// <summary>Called every frame by the game component; throttled, and only while on the colony map.</summary>
    public static void Tick()
    {
        if (RimlinkSession.State.Economy.Pending.Count == 0 || DateTime.UtcNow - lastAttempt < TimeSpan.FromSeconds(5)) return;
        DeliverPending();
    }

    public static void DeliverPending()
    {
        lastAttempt = DateTime.UtcNow;
        if (!RimlinkSession.Active || RimlinkSession.Economy is null || Current.ProgramState != ProgramState.Playing) return;
        var map = Find.CurrentMap;
        if (map is null || !map.IsPlayerHome) return;

        foreach (var parcel in RimlinkSession.State.Economy.Pending.ToList())
        {
            if (!parcel.Redeliver && spawnedThisSession.Contains(parcel.Id)) continue;
            List<Thing> things;
            try
            {
                things = Unpack(parcel);
            }
            catch (Exception ex)
            {
                RimlinkLog.Instance.Warn($"parcel {parcel.Id} could not be unpacked: {ex.Message}");
                Messages.Message("Rimlink.Economy.ParcelFailed".Translate(ex.Message), MessageTypeDefOf.NegativeEvent, historical: false);
                spawnedThisSession.Add(parcel.Id); // no retry loop every 5 s; the next login tries again
                continue;
            }

            var center = DropCenter(map);
            DropPodUtility.DropThingsNear(center, map, things, openDelay: 110, canInstaDropDuringInit: false, leaveSlag: false, canRoofPunch: true, forbid: false);
            spawnedThisSession.Add(parcel.Id);
            Messages.Message("Rimlink.Economy.ParcelDropped".Translate(parcel.From?.Name ?? ("Rimlink.Economy.Kind." + parcel.Kind).Translate().ToString()), new LookTargets(center, map), MessageTypeDefOf.PositiveEvent, historical: false);
            RimlinkLog.Instance.Info($"parcel {parcel.Id} dropped: {things.Count} things");

            long id = parcel.Id;
            long tick = Find.TickManager.TicksGame;
            var economy = RimlinkSession.Economy;
            _ = Task.Run(async () =>
            {
                try
                {
                    await economy.Deliver(id, tick, CancellationToken.None);
                    MainThread.Post(() =>
                    {
                        RimlinkSession.State.Economy.MarkDelivered(id);
                        SaveSync.TriggerSave("delivery");
                    });
                }
                catch (Exception ex)
                {
                    RimlinkLog.Instance.Warn($"could not confirm delivery of parcel {id}: {ex.Message}");
                }
            });
        }
    }

    private static List<Thing> Unpack(Parcel parcel)
    {
        var things = new List<Thing>();
        foreach (var item in parcel.Items.Items) things.Add(ThingBlobs.DeserializeItem(item));
        foreach (var pawn in parcel.Pawns.Pawns) things.Add(ThingBlobs.DeserializePawn(pawn));
        if (parcel.Kind == ParcelKind.ParcelWithdraw && parcel.Silver > 0)
        {
            long left = parcel.Silver;
            while (left > 0)
            {
                var silver = ThingMaker.MakeThing(ThingDefOf.Silver);
                silver.stackCount = (int)Math.Min(left, ThingDefOf.Silver.stackLimit);
                left -= silver.stackCount;
                things.Add(silver);
            }
        }
        return things;
    }

    private static IntVec3 DropCenter(Map map)
    {
        var beacon = map.listerBuildings.AllBuildingsColonistOfDef(ThingDefOf.OrbitalTradeBeacon).FirstOrDefault();
        return beacon is not null ? beacon.Position : DropCellFinder.TradeDropSpot(map);
    }
}
