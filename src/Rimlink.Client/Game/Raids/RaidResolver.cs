using Google.Protobuf;
using Grpc.Core;
using RimWorld;
using Rimlink.Client.Economy;
using Rimlink.Client.Game.Economy;
using Rimlink.Client.UI;
using Rimlink.Protocol.V1;
using Verse;

namespace Rimlink.Client.Game.Raids;

/// <summary>
/// Decides how a landed raid ended and reports it. A raider that leaves the map is captured at the exit (its state and
/// its load), one that dies or is imprisoned is read from the map, one still alive at the timeout is sent home. Loot is
/// trimmed to the cap before it leaves the map; the report is journaled, the game saved and uploaded, then sent.
/// Main thread only.
/// </summary>
public static class RaidResolver
{
    /// <summary>Mirrors the server default Raids:TimeoutDays; the server validates the report either way.</summary>
    public const int TimeoutTicks = 3 * 60000;

    private static DateTime lastAttempt = DateTime.MinValue;

    public static void Tick()
    {
        if (DateTime.UtcNow - lastAttempt < TimeSpan.FromSeconds(5)) return;
        lastAttempt = DateTime.UtcNow;
        if (!RimlinkSession.Active || RimlinkSession.Raids is null || Current.ProgramState != ProgramState.Playing) return;
        var map = Find.CurrentMap;
        if (map is null || !map.IsPlayerHome) return;
        var component = Current.Game.GetComponent<RimlinkGameComponent>();
        foreach (var raid in component.ActiveRaids.ToList())
        {
            if (raid.Reported) { component.ActiveRaids.Remove(raid); continue; }
            if (IsOver(raid, map)) Resolve(raid, map, component);
        }
    }

    /// <summary>Called from the ExitMap prefix: a raider leaving with its load is a FLED outcome, recorded now because the pawn may be discarded.</summary>
    public static void OnPawnExiting(Pawn pawn)
    {
        if (Current.ProgramState != ProgramState.Playing || Current.Game is null) return;
        var component = Current.Game.GetComponent<RimlinkGameComponent>();
        if (component is null || component.ActiveRaids.Count == 0) return;
        var raid = component.ActiveRaids.FirstOrDefault(r => r.PawnIds.Contains(pawn.thingIDNumber));
        if (raid is null || raid.Exits.Any(e => e.PawnId == pawn.thingIDNumber)) return;
        try
        {
            var loot = TakeLoad(pawn);
            raid.Exits.Add(new RaiderExit
            {
                PawnId = pawn.thingIDNumber,
                PawnBlob = raid.PawnMode == (int)PawnMode.PawnReal ? ThingBlobs.SerializePawn(pawn).ToByteArray() : [],
                Loot = loot.ToByteArray(),
            });
            RimlinkLog.Instance.Info($"raider {pawn.LabelShort} of raid {raid.RaidId} fled with {loot.Items.Count} stacks");
        }
        catch (Exception ex)
        {
            Log.Error("[Rimlink] could not record a fleeing raider: " + ex);
        }
    }

    private static bool IsOver(ActiveRaid raid, Map map)
    {
        if (Find.TickManager.TicksGame >= raid.LandedTick + TimeoutTicks) return true;
        if (raid.Lord is null || raid.Lord.ownedPawns.Count == 0) return true;
        return raid.PawnIds.All(id => raid.Exits.Any(e => e.PawnId == id) || FindRaider(id, map) is not { } p || p.Dead || p.IsPrisonerOfColony || !p.Spawned);
    }

    private static Pawn? FindRaider(int id, Map map) =>
        map.mapPawns.AllPawns.FirstOrDefault(p => p.thingIDNumber == id)
        ?? Find.WorldPawns.AllPawnsAliveOrDead.FirstOrDefault(p => p.thingIDNumber == id);

    private static void Resolve(ActiveRaid raid, Map map, RimlinkGameComponent component)
    {
        if (!RimlinkSession.Active || RimlinkSession.Raids is not { } raids) return;
        var report = new RaidReport { RaidId = raid.RaidId, Loot = new ItemList(), Captives = new PawnList() };
        var loot = new List<ItemBlob>();
        var summary = new RaidSummary();
        try
        {
            for (int index = 0; index < raid.PawnIds.Count; index++)
            {
                int id = raid.PawnIds[index];
                var outcome = new PawnOutcomeMsg { Index = index };
                if (raid.Exits.FirstOrDefault(e => e.PawnId == id) is { } exit)
                {
                    outcome.Outcome = PawnOutcome.OutcomeFled;
                    if (exit.PawnBlob.Length > 0) outcome.Blob = PawnBlob.Parser.ParseFrom(exit.PawnBlob);
                    loot.AddRange(ItemList.Parser.ParseFrom(exit.Loot).Items);
                    summary.Fled++;
                }
                else if (FindRaider(id, map) is { } pawn && !pawn.Dead && pawn.IsPrisonerOfColony)
                {
                    outcome.Outcome = PawnOutcome.OutcomeCaptured;
                    summary.Captured++;
                }
                else if (FindRaider(id, map) is { } alive && !alive.Dead && alive.Spawned)
                {
                    // Still on the map at the timeout: they go home with what they carry.
                    outcome.Outcome = PawnOutcome.OutcomeAlive;
                    loot.AddRange(TakeLoad(alive).Items);
                    if (raid.PawnMode == (int)PawnMode.PawnReal) outcome.Blob = ThingBlobs.SerializePawn(alive);
                    alive.DeSpawn();
                    alive.Destroy(DestroyMode.Vanish);
                    summary.Alive++;
                }
                else
                {
                    outcome.Outcome = PawnOutcome.OutcomeDead;
                    summary.Dead++;
                }
                report.Outcomes.Add(outcome);
            }

            // Loot within the cap; the rest is dropped at the edge, as if abandoned in the flight.
            long value = 0;
            foreach (var item in loot.OrderByDescending(i => i.MarketValueX100 * i.Count))
            {
                long worth = item.MarketValueX100 * item.Count / 100;
                if (value + worth <= raid.LootCap) { report.Loot.Items.Add(item); value += worth; }
                else PutBack(item, map);
            }
            summary.LootValue = value;

            if (raid.AllowKidnap)
            {
                var kidnapped = RimlinkFactions.Raiders.kidnapped.KidnappedPawnsListForReading.ToList();
                foreach (var captive in kidnapped)
                {
                    RimlinkFactions.Raiders.kidnapped.RemoveKidnappedPawn(captive);
                    if (report.Captives.Pawns.Count == 0)
                    {
                        report.Captives.Pawns.Add(ThingBlobs.SerializePawn(captive));
                        captive.Destroy(DestroyMode.Vanish);
                        summary.Captives++;
                    }
                    else GenSpawn.Spawn(captive, CellFinder.RandomEdgeCell(map), map); // only one may be taken; the others escaped
                }
            }
            raid.Lord?.Cleanup();
        }
        catch (Exception ex)
        {
            Log.Error($"[Rimlink] could not resolve raid {raid.RaidId}: {ex}");
            raid.LandedTick = Find.TickManager.TicksGame; // try again later rather than every 5 s
            return;
        }

        report.GameTick = Find.TickManager.TicksGame;
        var outbox = RimlinkSession.Outbox;
        var entry = outbox?.Add(report);
        raid.Reported = true;
        component.ActiveRaids.Remove(raid);
        Messages.Message("Rimlink.Raid.Resolved".Translate(raid.AttackerName, summary.Dead, summary.Captured, summary.Fled + summary.Alive, summary.LootValue), MessageTypeDefOf.NeutralEvent, historical: true);
        RimlinkLog.Instance.Info($"raid {raid.RaidId} resolved: dead {summary.Dead}, captured {summary.Captured}, fled {summary.Fled}, alive {summary.Alive}, loot {summary.LootValue}, captives {summary.Captives}");

        // The save without the loot and the survivors must reach the server before the report does.
        if (!SaveSync.TriggerSave("raid-resolved"))
        {
            RimlinkLog.Instance.Warn("raid report waits in the outbox: no save could be made now");
            return;
        }
        bool uploaded = false;
        LongEventHandler.QueueLongEvent(() => uploaded = SaveSync.WaitForUpload(TimeSpan.FromSeconds(60)), "Rimlink.Flow.Uploading", doAsynchronously: true, null, showExtraUIInfo: false, callback: () =>
        {
            if (!uploaded || SaveSync.LastError is not null)
            {
                RimlinkLog.Instance.Warn("raid report waits in the outbox: the save was not uploaded");
                return;
            }
            _ = Task.Run(async () =>
            {
                try
                {
                    await raids.Report(report, CancellationToken.None);
                    if (entry is not null) outbox!.Remove(entry.Id);
                }
                catch (RpcException ex) when (TransferOutbox.IsDefinitive(ex))
                {
                    RimlinkLog.Instance.Warn($"raid report refused ({EconomyClient.MessageOf(ex)}); dropped");
                    if (entry is not null) outbox!.Remove(entry.Id);
                    if (EconomyClient.CodeOf(ex) == ErrorCode.LootOverCap)
                        MainThread.Post(() => { foreach (var item in report.Loot.Items) PutBack(item, map); SaveSync.TriggerSave("raid-loot-returned"); });
                }
                catch (Exception ex)
                {
                    RimlinkLog.Instance.Warn($"raid report unanswered, kept in the outbox: {ex.Message}");
                }
            });
        });
    }

    /// <summary>Takes what the pawn carries and holds out of the game as blobs (apparel and weapons stay on the pawn).</summary>
    private static ItemList TakeLoad(Pawn pawn)
    {
        var list = new ItemList();
        var things = new List<Thing>();
        if (pawn.carryTracker?.CarriedThing is { } carried) { pawn.carryTracker.innerContainer.Remove(carried); things.Add(carried); }
        if (pawn.inventory is not null)
        {
            foreach (var t in pawn.inventory.innerContainer.ToList()) { pawn.inventory.innerContainer.Remove(t); things.Add(t); }
        }
        foreach (var t in things)
        {
            if (t is Pawn) continue; // carried pawns (kidnap victims) are handled by the kidnap tracker
            list.Items.Add(ThingBlobs.SerializeItem(t));
            t.Destroy(DestroyMode.Vanish);
        }
        return list;
    }

    private static void PutBack(ItemBlob item, Map map)
    {
        try
        {
            GenPlace.TryPlaceThing(ThingBlobs.DeserializeItem(item), CellFinder.RandomEdgeCell(map), map, ThingPlaceMode.Near);
        }
        catch (Exception ex)
        {
            Log.Error("[Rimlink] could not put loot back on the map: " + ex);
        }
    }
}
