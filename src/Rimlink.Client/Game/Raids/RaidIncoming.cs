using Grpc.Core;
using RimWorld;
using Rimlink.Client.Economy;
using Rimlink.Client.Game.Economy;
using Rimlink.Client.UI;
using Rimlink.Protocol.V1;
using Verse;
using Verse.AI.Group;

namespace Rimlink.Client.Game.Raids;

/// <summary>
/// Raids aimed at this player become vanilla raids on the home map: announced (letter and a one-day countdown in this
/// game's ticks, or at once), then fetched in full and spawned under the raider faction with an assault lord. The
/// landing is journaled in the outbox before it is confirmed; the raid itself is remembered in the game component so a
/// reload still resolves it. Main thread only.
/// </summary>
public static class RaidIncoming
{
    private static readonly HashSet<long> announced = [];
    private static readonly HashSet<long> landedThisSession = [];
    private static readonly HashSet<long> busy = [];
    private static DateTime lastAttempt = DateTime.MinValue;

    public static void Reset()
    {
        announced.Clear();
        landedThisSession.Clear();
        busy.Clear();
    }

    /// <summary>Called every frame by the game component; throttled, and only on the colony map.</summary>
    public static void Tick()
    {
        if (DateTime.UtcNow - lastAttempt < TimeSpan.FromSeconds(5)) return;
        lastAttempt = DateTime.UtcNow;
        if (!RimlinkSession.Active || Visits.VisitSession.InVisit || RimlinkSession.Raids is not { } raids || Current.ProgramState != ProgramState.Playing) return;
        var map = Find.CurrentMap;
        if (map is null || !map.IsPlayerHome) return;
        var component = Current.Game.GetComponent<RimlinkGameComponent>();

        foreach (var raid in RimlinkSession.State.Raids.Incoming.ToList())
        {
            long id = raid.Id;
            if (busy.Contains(id) || landedThisSession.Contains(id) || component.ActiveRaids.Any(a => a.RaidId == id)) continue;
            if (raid.State == RaidState.RaidQueued)
            {
                if (!announced.Add(id)) continue;
                Announce(raids, raid);
                continue;
            }
            if (raid.State == RaidState.RaidAnnounced && Find.TickManager.TicksGame >= raid.LandAtTick)
            {
                busy.Add(id);
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var full = await raids.Get(id, CancellationToken.None);
                        MainThread.Post(() => Land(full, map, raids));
                    }
                    catch (RpcException ex) when (EconomyClient.CodeOf(ex) == ErrorCode.RaidGone || ex.StatusCode == StatusCode.NotFound)
                    {
                        RimlinkLog.Instance.Warn($"raid {id} is gone before landing: {EconomyClient.MessageOf(ex)}");
                        MainThread.Post(() => { busy.Remove(id); RimlinkSession.State.Raids.MarkLanded(id); });
                    }
                    catch (Exception ex)
                    {
                        RimlinkLog.Instance.Warn($"raid {id} could not be fetched, retrying: {ex.Message}");
                        MainThread.Post(() => busy.Remove(id));
                    }
                });
            }
        }
    }

    private static void Announce(Rimlink.Client.Raids.RaidClient raids, Raid raid)
    {
        long id = raid.Id;
        long tick = Find.TickManager.TicksGame;
        _ = Task.Run(async () =>
        {
            try
            {
                var updated = await raids.Announce(id, tick, CancellationToken.None);
                MainThread.Post(() =>
                {
                    RimlinkSession.State.Raids.Update(updated);
                    if (updated.Timing == RaidTiming.TimingWarned)
                    {
                        string from = updated.Attacker?.Name ?? "?";
                        Find.LetterStack.ReceiveLetter("Rimlink.Raid.WarningLabel".Translate(from), "Rimlink.Raid.Warning".Translate(from, updated.Pawns.Pawns.Count), LetterDefOf.ThreatSmall);
                    }
                    RimlinkLog.Instance.Info($"raid {id} announced at tick {tick}, lands at {updated.LandAtTick}");
                });
            }
            catch (RpcException ex) when (EconomyClient.CodeOf(ex) == ErrorCode.RaidGone)
            {
                RimlinkLog.Instance.Warn($"raid {id} is gone: {EconomyClient.MessageOf(ex)}");
                MainThread.Post(() => RimlinkSession.State.Raids.MarkLanded(id));
            }
            catch (Exception ex)
            {
                RimlinkLog.Instance.Warn($"raid {id} could not be announced, retrying: {ex.Message}");
                MainThread.Post(() => announced.Remove(id));
            }
        });
    }

    /// <summary>Spawns the raiders and confirms the landing (journaled first, so a lost answer is replayed).</summary>
    private static void Land(Raid raid, Map map, Rimlink.Client.Raids.RaidClient raids)
    {
        long id = raid.Id;
        busy.Remove(id);
        if (!RimlinkSession.Active || Visits.VisitSession.InVisit || Current.ProgramState != ProgramState.Playing || !Find.Maps.Contains(map) || landedThisSession.Contains(id)) return;
        var component = Current.Game.GetComponent<RimlinkGameComponent>();
        if (component.ActiveRaids.Any(a => a.RaidId == id)) return;
        if (raid.State is not (RaidState.RaidAnnounced or RaidState.RaidQueued))
        {
            // Already landed (an earlier landing never reached a save) or over: the server will not take a second landing, so no second spawn.
            RimlinkLog.Instance.Warn($"raid {id} is {raid.State} on the server; not spawning it again");
            landedThisSession.Add(id);
            RimlinkSession.State.Raids.MarkLanded(id);
            return;
        }

        List<Pawn> pawns;
        Faction faction;
        try
        {
            faction = RimlinkFactions.Raiders;
            if (Economy.Incoming.MissingDefs(null, raid.Pawns) is { } missing) throw new InvalidDataException(missing);
            pawns = raid.Pawns.Pawns.Select(b => ThingBlobs.DeserializePawn(b, newIds: true, joins: faction)).ToList();
        }
        catch (Exception ex)
        {
            RimlinkLog.Instance.Warn($"raid {id} could not be unpacked: {ex.Message}");
            Messages.Message("Rimlink.Raid.Failed".Translate(ex.Message), MessageTypeDefOf.NegativeEvent, historical: false);
            landedThisSession.Add(id); // no retry loop every 5 s; the next login tries again
            return;
        }

        var lord = LordMaker.MakeNewLord(faction, new LordJob_AssaultColony(faction, canKidnap: raid.AllowKidnap, canTimeoutOrFlee: true, sappers: false, useAvoidGridSmart: true, canSteal: true), map, pawns);
        IntVec3 center;
        if (raid.Arrival == RaidArrival.ArrivalPods)
        {
            center = DropCellFinder.FindRaidDropCenterDistant(map);
            DropPodUtility.DropThingsNear(center, map, pawns, openDelay: 110, canInstaDropDuringInit: false, leaveSlag: true, canRoofPunch: true, forbid: false);
        }
        else
        {
            if (!RCellFinder.TryFindRandomPawnEntryCell(out center, map, CellFinder.EdgeRoadChance_Hostile)) center = CellFinder.RandomEdgeCell(map);
            foreach (var pawn in pawns) GenSpawn.Spawn(pawn, CellFinder.RandomClosewalkCellNear(center, map, 6), map);
        }
        landedThisSession.Add(id);
        string from = raid.Attacker?.Name ?? "?";
        Find.LetterStack.ReceiveLetter("Rimlink.Raid.LandedLabel".Translate(from), "Rimlink.Raid.Landed".Translate(from, pawns.Count), LetterDefOf.ThreatBig, new LookTargets(center, map));
        RimlinkLog.Instance.Info($"raid {id} landed: {pawns.Count} raiders ({raid.Arrival})");

        long tick = Find.TickManager.TicksGame;
        component.ActiveRaids.Add(new ActiveRaid
        {
            RaidId = id,
            AttackerPlayerId = raid.Attacker?.PlayerId ?? 0,
            AttackerName = from,
            Lord = lord,
            LandedTick = tick,
            PawnIds = pawns.Select(p => p.thingIDNumber).ToList(),
            ArrivedThingIds = pawns.SelectMany(RaidResolver.ArrivalLoadIds).ToList(),
            PawnMode = (int)raid.PawnMode,
            LootCap = raid.LootCap,
            AllowKidnap = raid.AllowKidnap,
        });
        RimlinkSession.State.Raids.MarkLanded(id);

        var outbox = RimlinkSession.Outbox;
        var entry = outbox?.Add(new RaidTick { RaidId = id, GameTick = tick });
        _ = Task.Run(async () =>
        {
            try
            {
                await raids.Land(id, tick, CancellationToken.None);
                if (entry is not null) outbox!.Remove(entry.Id);
            }
            catch (RpcException ex) when (TransferOutbox.IsDefinitive(ex))
            {
                RimlinkLog.Instance.Warn($"landing of raid {id} refused ({EconomyClient.MessageOf(ex)}); the raiders stay");
                if (entry is not null) outbox!.Remove(entry.Id);
            }
            catch (Exception ex)
            {
                RimlinkLog.Instance.Warn($"landing of raid {id} unconfirmed, kept in the outbox: {ex.Message}");
            }
            MainThread.Post(() => SaveSync.TriggerSave("raid-landed"));
        });
    }
}

/// <summary>A raid on this map, remembered across saves so its outcome is reported even after a reload.</summary>
public sealed class ActiveRaid : IExposable
{
    public long RaidId;
    public long AttackerPlayerId;
    public string AttackerName = "";
    public Lord? Lord;
    public long LandedTick;
    public List<int> PawnIds = [];
    /// <summary>Thing ids of what the raiders brought (inventory, hands) at the landing: theirs, not loot. Empty for raids landed by older versions.</summary>
    public List<int> ArrivedThingIds = [];
    public int PawnMode;
    public long LootCap;
    public bool AllowKidnap;
    /// <summary>Live sessions (3b): the session this raid belongs to (0 for a plain 3a raid); a visit sends every living pawn home with no loot.</summary>
    public long LiveSessionId;
    public bool Visit;
    /// <summary>Set once the report left this game (outbox or answered), so it is not reported twice.</summary>
    public bool Reported;
    /// <summary>Raiders that left the map, with their state and load at the exit.</summary>
    public List<RaiderExit> Exits = [];

    public void ExposeData()
    {
        Scribe_Values.Look(ref RaidId, "raidId");
        Scribe_Values.Look(ref AttackerPlayerId, "attackerPlayerId");
        Scribe_Values.Look(ref AttackerName, "attackerName", "");
        Scribe_References.Look(ref Lord, "lord");
        Scribe_Values.Look(ref LandedTick, "landedTick");
        Scribe_Collections.Look(ref PawnIds, "pawnIds", LookMode.Value);
        Scribe_Collections.Look(ref ArrivedThingIds, "arrivedThingIds", LookMode.Value);
        Scribe_Values.Look(ref PawnMode, "pawnMode");
        Scribe_Values.Look(ref LootCap, "lootCap");
        Scribe_Values.Look(ref AllowKidnap, "allowKidnap");
        Scribe_Values.Look(ref LiveSessionId, "liveSessionId");
        Scribe_Values.Look(ref Visit, "visit");
        Scribe_Values.Look(ref Reported, "reported");
        Scribe_Collections.Look(ref Exits, "exits", LookMode.Deep);
        PawnIds ??= [];
        ArrivedThingIds ??= [];
        Exits ??= [];
    }
}

public sealed class RaiderExit : IExposable
{
    public int PawnId;
    public byte[] PawnBlob = [];
    public byte[] Loot = [];

    public void ExposeData()
    {
        Scribe_Values.Look(ref PawnId, "pawnId");
        DataExposeUtility.LookByteArray(ref PawnBlob, "pawnBlob");
        DataExposeUtility.LookByteArray(ref Loot, "loot");
        PawnBlob ??= [];
        Loot ??= [];
    }
}
