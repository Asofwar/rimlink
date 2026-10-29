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
/// its load), one that dies or is imprisoned is read from the map, one still alive at the timeout is sent home. The
/// report is built and journaled before anything leaves the map, loot is trimmed to the cap with the server's own
/// formula, then the game is saved and uploaded and the report sent. Main thread only.
/// </summary>
public static class RaidResolver
{
    /// <summary>Mirrors the server default Raids:TimeoutDays; the server validates the report either way.</summary>
    public const int TimeoutTicks = 3 * 60000;

    private static DateTime lastAttempt = DateTime.MinValue;

    /// <summary>A stack of loot: still a thing on this map, or already a blob (taken at a raider's exit).</summary>
    private sealed record LootStack(ItemBlob Blob, Thing? Thing, Pawn? Carrier);

    public static void Tick()
    {
        if (DateTime.UtcNow - lastAttempt < TimeSpan.FromSeconds(5)) return;
        lastAttempt = DateTime.UtcNow;
        if (!RimlinkSession.Active || Visits.VisitSession.InVisit || RimlinkSession.Raids is null || Current.ProgramState != ProgramState.Playing) return;
        var component = Current.Game.GetComponent<RimlinkGameComponent>();
        foreach (var raid in component.ActiveRaids.ToList())
        {
            if (raid.Reported) { component.ActiveRaids.Remove(raid); continue; }
            if (MapOf(raid) is not { } map) continue; // the raided map is not loaded (a caravan is playing elsewhere); it waits
            if (IsOver(raid, map)) Resolve(raid, map, component);
        }
    }

    /// <summary>The map the raid landed on: the lord's, else whichever home map still holds a raider, else the only home map.</summary>
    private static Map? MapOf(ActiveRaid raid)
    {
        if (raid.Lord?.Map is { } lordMap && Find.Maps.Contains(lordMap)) return lordMap;
        var homes = Find.Maps.Where(m => m.IsPlayerHome).ToList();
        return homes.FirstOrDefault(m => raid.PawnIds.Any(id => m.mapPawns.AllPawns.Any(p => p.thingIDNumber == id)))
            ?? (homes.Count == 1 ? homes[0] : null);
    }

    /// <summary>Called from the ExitMap prefix: a raider leaving with its load is a FLED outcome, recorded now because the pawn may be discarded.</summary>
    public static void OnPawnExiting(Pawn pawn)
    {
        if (Current.ProgramState != ProgramState.Playing || Current.Game is null) return;
        if (Live.LiveHost.TryRecordExit(pawn)) return;
        var component = Current.Game.GetComponent<RimlinkGameComponent>();
        if (component is null || component.ActiveRaids.Count == 0) return;
        var raid = component.ActiveRaids.FirstOrDefault(r => r.PawnIds.Contains(pawn.thingIDNumber));
        if (raid is null || raid.Exits.Any(e => e.PawnId == pawn.thingIDNumber)) return;
        try
        {
            raid.Exits.Add(RecordExit(pawn, raid.PawnMode == (int)PawnMode.PawnReal, raid.Visit));
            RimlinkLog.Instance.Info($"raider {pawn.LabelShort} of raid {raid.RaidId} fled");
        }
        catch (Exception ex)
        {
            Log.Error("[Rimlink] could not record a fleeing raider: " + ex);
        }
    }

    /// <summary>A pawn leaving the map, serialised with its load (a visitor's load stays behind); the load is destroyed here because the pawn may be discarded.</summary>
    public static RaiderExit RecordExit(Pawn pawn, bool realPawn, bool visit)
    {
        var load = LoadOf(pawn);
        if (visit) { pawn.inventory?.DropAllNearPawn(pawn.PositionHeld); load = LoadOf(pawn); }
        var loot = new ItemList();
        foreach (var t in load) loot.Items.Add(ThingBlobs.SerializeItem(t));
        var exit = new RaiderExit
        {
            PawnId = pawn.thingIDNumber,
            PawnBlob = realPawn ? ThingBlobs.SerializePawn(pawn).ToByteArray() : [],
            Loot = loot.ToByteArray(),
        };
        foreach (var t in load) { t.holdingOwner?.Remove(t); t.Destroy(DestroyMode.Vanish); }
        return exit;
    }

    /// <summary>
    /// Over at the timeout, or once no raider can still act: each one left the map, died, is a prisoner, or is simply gone.
    /// A downed raider lying on the map, or one being carried to a bed or a cell, keeps the raid open: the colony may still capture them.
    /// </summary>
    private static bool IsOver(ActiveRaid raid, Map map)
    {
        if (Find.TickManager.TicksGame >= raid.LandedTick + TimeoutTicks) return true;
        return raid.PawnIds.All(id => raid.Exits.Any(e => e.PawnId == id) || FindRaider(id, map) is not { } p || p.Dead || p.IsPrisonerOfColony || !OnTheMap(p));
    }

    private static bool OnTheMap(Pawn p) => p.Spawned || p.ParentHolder is Pawn_CarryTracker;

    private static Pawn? FindRaider(int id, Map map) =>
        map.mapPawns.AllPawns.FirstOrDefault(p => p.thingIDNumber == id)
        ?? Find.WorldPawns.AllPawnsAliveOrDead.FirstOrDefault(p => p.thingIDNumber == id);

    /// <summary>A live session ended (3b): its guest pawns are reported now, with no timeout; the raid is not in the active list.</summary>
    public static void ResolveNow(ActiveRaid raid, Map map) => Resolve(raid, map, Current.Game.GetComponent<RimlinkGameComponent>());

    private static void Resolve(ActiveRaid raid, Map map, RimlinkGameComponent component)
    {
        if (!RimlinkSession.Active || RimlinkSession.Raids is not { } raids) return;
        var report = new RaidReport { RaidId = raid.RaidId, LiveSessionId = raid.LiveSessionId, Loot = new ItemList(), Captives = new PawnList() };
        var loot = new List<LootStack>();
        var kept = new List<LootStack>();
        var survivors = new List<Pawn>();
        var captives = new List<Pawn>();
        Pawn? captive = null;
        var summary = new RaidSummary();

        // Phase 1: read everything into the report without touching the map, so a failure here changes nothing.
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
                    foreach (var item in ItemList.Parser.ParseFrom(exit.Loot).Items) loot.Add(new LootStack(item, null, null));
                    summary.Fled++;
                }
                else if (FindRaider(id, map) is { } pawn && !pawn.Dead && pawn.IsPrisonerOfColony)
                {
                    outcome.Outcome = PawnOutcome.OutcomeCaptured;
                    summary.Captured++;
                }
                else if (FindRaider(id, map) is { } alive && !alive.Dead && OnTheMap(alive))
                {
                    if (raid.Visit)
                    {
                        // A visitor goes home as they came: the host's things stay (dropped where the pawn stands), nothing counts as loot.
                        alive.inventory?.DropAllNearPawn(alive.PositionHeld);
                        outcome.Outcome = PawnOutcome.OutcomeFled;
                        outcome.Blob = ThingBlobs.SerializePawn(alive);
                        survivors.Add(alive);
                        summary.Fled++;
                    }
                    else
                    {
                        // Still on the map at the timeout (fighting, downed or being carried): they go home with what they carry.
                        outcome.Outcome = PawnOutcome.OutcomeAlive;
                        foreach (var t in LoadOf(alive)) loot.Add(new LootStack(ThingBlobs.SerializeItem(t), t, alive));
                        if (raid.PawnMode == (int)PawnMode.PawnReal) outcome.Blob = ThingBlobs.SerializePawn(alive);
                        survivors.Add(alive);
                        summary.Alive++;
                    }
                }
                else
                {
                    outcome.Outcome = PawnOutcome.OutcomeDead;
                    summary.Dead++;
                }
                report.Outcomes.Add(outcome);
            }

            if (raid.AllowKidnap)
            {
                captives = RimlinkFactions.Raiders.kidnapped.KidnappedPawnsListForReading.ToList();
                captive = captives.FirstOrDefault(); // only one may be taken; the others escaped
                if (captive is not null) { report.Captives.Pawns.Add(ThingBlobs.SerializePawn(captive)); summary.Captives++; }
            }

            // Loot within the cap, in hundredths of silver like the server counts it; the rest stays with the colony.
            long valueX100 = 0;
            var left = new List<LootStack>();
            foreach (var stack in loot.OrderByDescending(s => s.Blob.MarketValueX100 * s.Blob.Count))
            {
                long worth = stack.Blob.MarketValueX100 * stack.Blob.Count;
                if (valueX100 + worth <= raid.LootCap * 100) { kept.Add(stack); valueX100 += worth; }
                else left.Add(stack);
            }
            foreach (var stack in kept) report.Loot.Items.Add(stack.Blob);
            summary.LootValue = valueX100 / 100;
            loot = left;
        }
        catch (Exception ex)
        {
            Log.Error($"[Rimlink] could not resolve raid {raid.RaidId}: {ex}");
            raid.LandedTick = Find.TickManager.TicksGame; // try again later rather than every 5 s
            return;
        }

        report.GameTick = Find.TickManager.TicksGame;
        var outbox = RimlinkSession.Outbox;
        var entry = outbox?.Add(report); // journaled before anything leaves the map
        raid.Reported = true;
        component.ActiveRaids.Remove(raid); // no-op for a live end resolved on the spot

        // Phase 2: what the report says is gone leaves the map; what it does not take stays where it was.
        try
        {
            foreach (var stack in loot.Where(s => s.Thing is null)) PutBack(stack.Blob, map); // taken at an exit, over the cap
            foreach (var stack in loot.Where(s => s.Thing is not null))
            {
                stack.Thing!.holdingOwner?.Remove(stack.Thing);
                GenPlace.TryPlaceThing(stack.Thing, stack.Carrier?.PositionHeld ?? CellFinder.RandomEdgeCell(map), map, ThingPlaceMode.Near);
            }
            foreach (var stack in kept.Where(s => s.Thing is not null))
            {
                stack.Thing!.holdingOwner?.Remove(stack.Thing);
                stack.Thing.Destroy(DestroyMode.Vanish);
            }
            foreach (var pawn in survivors)
            {
                if (pawn.Spawned) pawn.DeSpawn();
                pawn.holdingOwner?.Remove(pawn);
                pawn.Destroy(DestroyMode.Vanish);
            }
            foreach (var p in captives)
            {
                RimlinkFactions.Raiders.kidnapped.RemoveKidnappedPawn(p);
                if (p == captive) p.Destroy(DestroyMode.Vanish);
                else GenSpawn.Spawn(p, CellFinder.RandomEdgeCell(map), map);
            }
            raid.Lord?.Cleanup();
        }
        catch (Exception ex)
        {
            Log.Error($"[Rimlink] raid {raid.RaidId} was reported but the map could not be cleaned up fully: {ex}");
        }
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
            _ = Task.Run(() => Send(raids, outbox, entry, report));
        });
    }

    /// <summary>
    /// Sends the report. A refusal marks the entry so the loot comes back to the colony by pod (Outgoing.ReturnRefused);
    /// unless the raid is gone, a loot-less report is then sent once more so the raid still resolves for the attacker.
    /// </summary>
    private static async Task Send(Rimlink.Client.Raids.RaidClient raids, TransferOutbox? outbox, OutboxEntry? entry, RaidReport report)
    {
        try
        {
            await raids.Report(report, CancellationToken.None);
            if (entry is not null) outbox!.Remove(entry.Id);
            return;
        }
        catch (RpcException ex) when (TransferOutbox.IsDefinitive(ex))
        {
            string reason = EconomyClient.MessageOf(ex);
            RimlinkLog.Instance.Warn($"raid report refused ({reason}); the loot goes back to the colony");
            if (entry is not null) outbox!.MarkRefused(entry.Id, reason);
            if (EconomyClient.CodeOf(ex) == ErrorCode.RaidGone || report.Loot.Items.Count == 0) return;
        }
        catch (Exception ex)
        {
            RimlinkLog.Instance.Warn($"raid report unanswered, kept in the outbox: {ex.Message}");
            return;
        }

        var retry = report.Clone();
        retry.Loot.Items.Clear();
        var again = outbox?.Add(retry);
        try
        {
            await raids.Report(retry, CancellationToken.None);
            if (again is not null) outbox!.Remove(again.Id);
            RimlinkLog.Instance.Info($"raid {report.RaidId} reported without its loot");
        }
        catch (RpcException ex) when (TransferOutbox.IsDefinitive(ex))
        {
            if (again is not null) outbox!.Remove(again.Id);
            RimlinkLog.Instance.Error($"raid {report.RaidId} report refused again ({EconomyClient.MessageOf(ex)}); the attacker's pawns come home when the raid expires on the server");
        }
        catch (Exception ex)
        {
            RimlinkLog.Instance.Warn($"raid report unanswered, kept in the outbox: {ex.Message}");
        }
    }

    /// <summary>What the pawn carries and holds (apparel and weapons stay on the pawn); a carried pawn is a kidnap victim and is not loot.</summary>
    private static List<Thing> LoadOf(Pawn pawn)
    {
        var things = new List<Thing>();
        if (pawn.carryTracker?.CarriedThing is { } carried and not Pawn) things.Add(carried);
        if (pawn.inventory is not null) things.AddRange(pawn.inventory.innerContainer.Where(t => t is not Pawn));
        return things;
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
