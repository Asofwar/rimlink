using RimWorld;
using Rimlink.Client.Game.Economy;
using Rimlink.Client.Game.Raids;
using Rimlink.Client.Live;
using Rimlink.Client.UI;
using Rimlink.Protocol.V1;
using Verse;
using Verse.AI.Group;

namespace Rimlink.Client.Game.Live;

/// <summary>
/// The host side of a live session (spec §5): spawns the guest's pawns, takes the snapshot, streams deltas every six
/// ticks, executes the guest's orders, keeps the tempo, and resolves the outcome through the 3a resolver when the
/// session ends (or degrades to an AI raid when the guest is gone for good). Main thread only.
/// </summary>
public static class LiveHost
{
    private const int DeltaTicks = 6, FullTicks = 600, MaxFrameBytes = 262144;
    private static readonly TimeSpan MinDeltaInterval = TimeSpan.FromMilliseconds(100);

    public static bool Active { get; private set; }
    public static bool Starting { get; private set; }
    public static LiveSession? Session { get; private set; }
    public static long GuestId => Session?.Guest?.PlayerId ?? 0;
    public static string GuestName => Session?.Guest?.Name ?? "?";
    public static Map? Map { get; private set; }
    public static bool GuestHere { get; private set; }
    public static int DeltasSent { get; private set; }

    private static DeltaCollector? collector;
    private static int lastDeltaTick, lastFullTick;
    private static DateTime lastDeltaAt;
    private static long lootCap;
    private static bool resyncing;
    private static DateTime lastResyncAt;
    private static DateTime lastGuestCheck;
    private static int emptyChecks;
    private static bool guestSeen;
    /// <summary>Deltas in a row the stream would not take whole; past <see cref="MaxRefusedDeltas"/> the diff is too big to ever fit and a snapshot replaces it.</summary>
    private static int refusedDeltas;
    private const int MaxRefusedDeltas = 30;
    /// <summary>Health signature of the last blob each guest pawn's mirror got (see <see cref="HealthSignature"/>).</summary>
    private static readonly Dictionary<int, int> sentHealth = [];
    /// <summary>
    /// Sessions this game already reported or handed to the AI. The server's ended event for such a session arrives after the
    /// host ended it itself; it is not a pending end (a second, empty report would end a degraded raid with its escrow).
    /// </summary>
    private static readonly HashSet<long> settled = [];

    public static bool Settled(long sessionId) => settled.Contains(sessionId);
    private static readonly TimeSpan ResyncCooldown = TimeSpan.FromSeconds(30);
    private static Lord? guestLord;
    /// <summary>Guest pawns that walked off the map during the session, with what they carried (reported as fled).</summary>
    private static readonly List<RaiderExit> exits = [];

    /// <summary>LiveStarted arrived and this player is the host: fetch the pawns, then spawn and snapshot on the main thread.</summary>
    public static void Begin(LiveSession session)
    {
        if (RimlinkSession.Live is { } client) { client.Ended -= OnStreamEnded; client.Ended += OnStreamEnded; }
        exits.Clear();
        if (Active || Starting || !RimlinkSession.Active || RimlinkSession.Live is not { } live || RimlinkSession.Raids is not { } raids) return;
        if (Find.AnyPlayerHomeMap is null || Visits.VisitSession.InVisit) { RimlinkLog.Instance.Warn("live: no home map to host on; aborting"); _ = live.Cancel(session.Id, CancellationToken.None); return; }
        Starting = true;
        Session = session;
        LiveTempo.UseBudget(session);
        Messages.Message("Rimlink.Live.Starting".Translate(session.Guest?.Name ?? "?"), MessageTypeDefOf.NeutralEvent, historical: false);
        _ = Task.Run(async () =>
        {
            try
            {
                PawnList pawns;
                long cap = 0;
                if (session.Kind == LiveKind.LiveRaid)
                {
                    var raid = await raids.Get(session.RaidId, CancellationToken.None);
                    pawns = raid.Pawns;
                    cap = raid.LootCap;
                }
                else pawns = await live.Pawns(session.Id, CancellationToken.None);
                MainThread.Post(() => Spawn(session, pawns, cap, live));
            }
            catch (Exception ex)
            {
                RimlinkLog.Instance.Warn("live: the guest's pawns could not be fetched: " + ex.Message);
                MainThread.Post(() => { Starting = false; Session = null; Messages.Message("Rimlink.Live.Failed".Translate(ex.Message), MessageTypeDefOf.NegativeEvent, historical: false); });
                _ = live.Cancel(session.Id, CancellationToken.None);
            }
        });
    }

    private static void Spawn(LiveSession session, PawnList pawns, long cap, LiveClient live)
    {
        var map = Find.AnyPlayerHomeMap;
        if (map is null || !RimlinkSession.Active) { Starting = false; Session = null; return; }
        Map = map;
        lootCap = cap;
        emptyChecks = 0;
        guestSeen = false;
        refusedDeltas = 0;
        sentHealth.Clear();
        var owners = MapComponent_LiveOwners.Of(map);
        var spawned = new List<Pawn>();
        try
        {
            var faction = session.Kind == LiveKind.LiveRaid ? RimlinkFactions.Raiders : Faction.OfPlayer;
            for (int i = 0; i < pawns.Pawns.Count; i++)
            {
                var pawn = ThingBlobs.DeserializePawn(pawns.Pawns[i], newIds: true, joins: faction);
                owners.Mark(pawn, session.Guest.PlayerId, i);
                owners.MarkCarried(pawn);
                spawned.Add(pawn);
            }
            // Always spawned (pods would still be closed in the snapshot and drop a second copy through the spawn hook): pods land at the pod spot, caravans at the edge.
            IntVec3 center;
            if (session.Arrival == RaidArrival.ArrivalPods) center = DropCellFinder.FindRaidDropCenterDistant(map);
            else if (!RCellFinder.TryFindRandomPawnEntryCell(out center, map, CellFinder.EdgeRoadChance_Hostile)) center = CellFinder.RandomEdgeCell(map);
            foreach (var pawn in spawned) GenSpawn.Spawn(pawn, CellFinder.RandomClosewalkCellNear(center, map, 6), map);
            // Raiders need a lord that keeps them idle: a lordless pawn of another faction leaves the map on its own.
            if (session.Kind == LiveKind.LiveRaid) guestLord = LordMaker.MakeNewLord(faction, new LordJob_RimlinkGuests(), map, spawned);
            Find.LetterStack.ReceiveLetter("Rimlink.Live.ArrivedLabel".Translate(session.Guest.Name), "Rimlink.Live.Arrived".Translate(session.Guest.Name, spawned.Count, session.Kind == LiveKind.LiveRaid ? "Rimlink.Live.KindRaid".Translate() : "Rimlink.Live.KindVisit".Translate()),
                session.Kind == LiveKind.LiveRaid ? LetterDefOf.ThreatBig : LetterDefOf.PositiveEvent, new LookTargets(center, map));
        }
        catch (Exception ex)
        {
            Log.Error("[Rimlink] live: the guest's pawns could not be spawned: " + ex);
            foreach (var p in spawned) PawnExit.Remove(p);
            owners.ForgetAll(session.Guest.PlayerId);
            Starting = false; Session = null;
            _ = live.Cancel(session.Id, CancellationToken.None);
            Messages.Message("Rimlink.Live.Failed".Translate(ex.Message), MessageTypeDefOf.NegativeEvent, historical: false);
            return;
        }
        LiveTempo.Begin();
        collector = new DeltaCollector(map);
        RimlinkLog.Instance.Info($"live session {session.Id}: {spawned.Count} guest pawns of {session.Guest.Name} spawned ({session.Kind}); taking the snapshot");
        Snapshot(live, first: true);
    }

    /// <summary>Saves and uploads the host's game and tells the guest which version to load; the mirror baseline is read at the same tick.</summary>
    private static void Snapshot(LiveClient live, bool first)
    {
        if (Session is not { } session || collector is null) return;
        if (!SaveSync.TriggerSave("live-snapshot"))
        {
            RimlinkLog.Instance.Warn("live: no snapshot could be saved now; retrying in a moment");
            LongEventHandler.QueueLongEvent(() => Thread.Sleep(1000), "Rimlink.Saving", doAsynchronously: true, null, showExtraUIInfo: false, callback: () => Snapshot(live, first));
            return;
        }
        var baseline = collector.FullScan(blobsForNew: false);
        baseline.SnapshotSeq = collector.Current.SnapshotSeq + 1;
        bool uploaded = false;
        LongEventHandler.QueueLongEvent(() => uploaded = SaveSync.WaitForUpload(TimeSpan.FromSeconds(90)), "Rimlink.Flow.Uploading", doAsynchronously: true, null, showExtraUIInfo: false, callback: () =>
        {
            if (!uploaded || SaveSync.LastError is not null)
            {
                RimlinkLog.Instance.Warn("live: the snapshot was not uploaded: " + (SaveSync.LastError ?? "timeout"));
                if (first) Abort(); else resyncing = false;
                return;
            }
            int version = SaveSync.LastVersion;
            collector.Commit(baseline);
            sentHealth.Clear(); // the guest loads its pawns from this snapshot
            lastDeltaTick = Find.TickManager.TicksGame;
            lastFullTick = lastDeltaTick;
            lastDeltaAt = DateTime.UtcNow;
            _ = Task.Run(async () =>
            {
                try
                {
                    live.HelloLetterPause = RimlinkMod.Settings.LetterPause;
                    if (first) await live.Open(session.Id, LiveClient.HostRole, version, CancellationToken.None);
                    live.Send(new LiveClientMsg { SnapshotReady = new SnapshotReady { SaveVersion = version } });
                    MainThread.Post(() =>
                    {
                        resyncing = false;
                        if (first) { Active = true; Starting = false; RimlinkLog.Instance.Info($"live session {session.Id}: snapshot v{version} announced, waiting for the guest"); }
                        else RimlinkLog.Instance.Info($"live session {session.Id}: resync snapshot v{version} announced");
                    });
                }
                catch (Exception ex)
                {
                    RimlinkLog.Instance.Warn("live: the stream could not be opened: " + ex.Message);
                    MainThread.Post(Abort);
                }
            });
        });
    }

    /// <summary>Called every frame by the game component.</summary>
    public static void Tick()
    {
        if (!Active || Session is not { } session || Map is not { } map || collector is null || RimlinkSession.Live is not { } live) return;
        if (!Find.Maps.Contains(map)) { RimlinkLog.Instance.Warn("live: the hosted map is gone; ending the session"); Abort(); return; }
        LiveTempo.Tick();
        for (int i = 0; i < 64 && live.Incoming.TryDequeue(out var frame); i++)
        {
            switch (frame!.MCase)
            {
                case LiveServerMsg.MOneofCase.Order:
                    var result = OrderExecutor.Apply(frame.Order, session.Guest.PlayerId, session.Kind, map);
                    live.Send(new LiveClientMsg { OrderResult = result });
                    if (!result.Ok) RimlinkLog.Instance.Info($"live: order {frame.Order.Seq} ({frame.Order.OCase}) refused: {result.Reason}");
                    break;
                case LiveServerMsg.MOneofCase.PeerHello:
                    GuestHere = true;
                    guestSeen = true;
                    LiveTempo.Rules.GuestThreshold = frame.PeerHello.LetterPause;
                    LiveTempo.WaitingPause = false; // the host's own pause (HostWish) stays as the host set it
                    LiveTempo.Apply();
                    Messages.Message("Rimlink.Live.GuestHere".Translate(session.Guest.Name), MessageTypeDefOf.PositiveEvent, historical: false);
                    break;
                case LiveServerMsg.MOneofCase.ResyncRequest:
                    if (!resyncing && DateTime.UtcNow - lastResyncAt >= ResyncCooldown) Resync(live);
                    else RimlinkLog.Instance.Info("live: resync request ignored (one is running or the last one was too recent)");
                    break;
                case LiveServerMsg.MOneofCase.PeerLost:
                    GuestHere = false;
                    LiveTempo.WaitingPause = true;
                    LiveTempo.Apply();
                    Messages.Message("Rimlink.Live.GuestLost".Translate(session.Guest.Name), MessageTypeDefOf.NegativeEvent, historical: false);
                    break;
                case LiveServerMsg.MOneofCase.PeerBack:
                    Messages.Message("Rimlink.Live.GuestBack".Translate(session.Guest.Name), MessageTypeDefOf.PositiveEvent, historical: false);
                    break;
                case LiveServerMsg.MOneofCase.End:
                    End(frame.End.Reason);
                    return;
                case LiveServerMsg.MOneofCase.Notice:
                    RimlinkLog.Instance.Warn("live: " + frame.Notice.Text);
                    break;
            }
        }
        // Not before the guest's first Hello: a guest still loading the mirror would miss the end and stay in the host's copy.
        if (guestSeen && !resyncing && DateTime.UtcNow - lastGuestCheck >= TimeSpan.FromSeconds(1))
        {
            lastGuestCheck = DateTime.UtcNow;
            // Two checks in a row, so the frame showing the last pawn's death or capture reaches the guest before the end does.
            if (!RaidResolver.NoneLeft(MapComponent_LiveOwners.Of(map).IdsOf(session.Guest.PlayerId).OfType<int>(), exits, map)) emptyChecks = 0;
            else if (++emptyChecks >= 2) { EndWithoutGuests(session); return; }
        }
        if (!GuestHere || resyncing) return;
        int tick = Find.TickManager.TicksGame;
        bool paused = Find.TickManager.Paused;
        if (tick - lastDeltaTick < DeltaTicks && !(paused && DateTime.UtcNow - lastDeltaAt > TimeSpan.FromSeconds(1))) return;
        // At high speed the diffs grow instead of their number: the stream drops deltas over its rate (20 per second on both
        // the client and the server), and a dropped diff is a change the guest never sees.
        if (DateTime.UtcNow - lastDeltaAt < MinDeltaInterval) return;
        SendDelta(live, tick, full: tick - lastFullTick >= FullTicks);
    }

    private static void SendDelta(LiveClient live, int tick, bool full)
    {
        if (collector is null || Session is null) return;
        try
        {
            var next = full ? collector.FullScan(blobsForNew: true) : collector.Next();
            var frames = DeltaCodec.Diff(collector.Current, next, MaxFrameBytes);
            if (collector.Damage.Count > 0) frames[0].Damage.AddRange(collector.Damage);
            if (collector.Cells.Count > 0) frames[0].Cells.AddRange(collector.Cells);
            if (collector.Letters.Count > 0) frames[0].Messages.AddRange(collector.Letters);
            var healthSent = new List<(int Id, int Health)>();
            if (full)
            {
                // The guest takes only the health from these blobs, and every reload of one logs vanilla warnings there (ideo
                // fallback, replaced verbs): a pawn's blob goes only when its hediffs changed since the last one.
                var owners = MapComponent_LiveOwners.Of(Map!);
                foreach (var p in Map!.mapPawns.AllPawnsSpawned.Where(owners.IsOwned))
                {
                    try
                    {
                        int health = HealthSignature(p);
                        if (sentHealth.TryGetValue(p.thingIDNumber, out int last) && last == health) continue;
                        frames[0].GuestPawnBlobs.Add(ThingBlobs.SerializePawn(p));
                        healthSent.Add((p.thingIDNumber, health));
                    }
                    catch { }
                }
            }
            // All frames or none: a refused delta is not the guest's state, and without the commit the next diff carries its changes again.
            if (!live.SendDelta(frames))
            {
                lastDeltaTick = tick;
                lastDeltaAt = DateTime.UtcNow;
                // A diff that keeps growing past what the stream takes (thousands of new things at once) never fits: a snapshot replaces it.
                if (++refusedDeltas >= MaxRefusedDeltas && !resyncing)
                {
                    RimlinkLog.Instance.Warn($"live: {refusedDeltas} deltas in a row did not fit the stream; sending a fresh snapshot instead");
                    Resync(live);
                }
                return;
            }
            refusedDeltas = 0;
            collector.Commit(next);
            foreach (var (id, health) in healthSent) sentHealth[id] = health;
            if (full) lastFullTick = tick;
            DeltasSent += frames.Count;
            lastDeltaTick = tick;
            lastDeltaAt = DateTime.UtcNow;
            if (DeltasSent % 100 == 1) RimlinkLog.Instance.Info($"live: sent {DeltasSent} frames (tick {tick}, {next.Pawns.Count} pawns, {next.Things.Count} things)");
        }
        catch (Exception ex)
        {
            Log.Error("[Rimlink] live: delta collection failed: " + ex);
            lastDeltaTick = tick;
            if (full) lastFullTick = tick; // a full scan that throws would otherwise be retried, and fail, on every delta
        }
    }

    /// <summary>A fresh snapshot for the guest (its request, or a diff that cannot be sent): paused until the guest's new Hello.</summary>
    private static void Resync(LiveClient live)
    {
        resyncing = true;
        refusedDeltas = 0;
        lastResyncAt = DateTime.UtcNow;
        GuestHere = false;
        LiveTempo.WaitingPause = true; // the host's play button does not lift it
        LiveTempo.Apply();
        Snapshot(live, first: false);
    }

    /// <summary>The pawn's hediffs (kind, body part, severity in hundredths): what the guest's mirror takes from a pawn blob.</summary>
    private static int HealthSignature(Pawn p)
    {
        var hash = new HashCode();
        foreach (var h in p.health.hediffSet.hediffs)
        {
            hash.Add(h.def.shortHash);
            hash.Add(h.Part?.Index ?? -1);
            hash.Add((int)(h.Severity * 100f));
        }
        return hash.ToHashCode();
    }

    /// <summary>From the ExitMap hook: a guest pawn leaving the map is recorded with its load, as the 3a resolver does for raiders.</summary>
    public static bool TryRecordExit(Pawn pawn)
    {
        if (!Active || Session is null || Map is not { } map || pawn.Map != map) return false;
        var owners = MapComponent_LiveOwners.Of(map);
        if (owners.OwnerOf(pawn) != Session.Guest.PlayerId || exits.Any(e => e.PawnId == pawn.thingIDNumber)) return false;
        if (Session.Kind == LiveKind.LiveVisit) StripForeignGear(pawn, owners, visit: true);
        exits.Add(RaidResolver.RecordExit(pawn, realPawn: true, Session.Kind == LiveKind.LiveVisit));
        RimlinkLog.Instance.Info($"live: {pawn.LabelShort} left the map");
        return true;
    }

    /// <summary>
    /// The host's things a guest pawn picked up during the session: a visitor drops them where they stand (nothing leaves a
    /// visit), a raider's go into the inventory so the 3a resolver counts them as loot under the cap.
    /// </summary>
    private static void StripForeignGear(Pawn pawn, MapComponent_LiveOwners owners, bool visit)
    {
        var pos = pawn.PositionHeld;
        foreach (var eq in pawn.equipment?.AllEquipmentListForReading.ToList() ?? [])
        {
            if (owners.IsCarried(eq)) continue;
            if (visit) pawn.equipment!.TryDropEquipment(eq, out _, pos);
            else { pawn.equipment!.Remove(eq); pawn.inventory?.innerContainer.TryAdd(eq); }
        }
        foreach (var ap in pawn.apparel?.WornApparel.ToList() ?? [])
        {
            if (owners.IsCarried(ap)) continue;
            if (visit) pawn.apparel!.TryDrop(ap, out _, pos);
            else { pawn.apparel!.Remove(ap); pawn.inventory?.innerContainer.TryAdd(ap); }
        }
    }

    /// <summary>The stream is gone for good (reconnect budget spent, the session no longer exists on the server).</summary>
    private static void OnStreamEnded(LiveEndReason reason) => MainThread.Post(() => { if (Active) End(reason); });

    /// <summary>
    /// No guest pawn is left to play (dead, captured or gone): the host ends the session with its own reason and reports it, instead
    /// of leaving the guest in a mirror with nothing to control (ruling P6 in the live spec).
    /// </summary>
    private static void EndWithoutGuests(LiveSession session)
    {
        RimlinkLog.Instance.Info($"live session {session.Id}: no pawn of {session.Guest.Name} is left to play; ending the session");
        if (RimlinkSession.Live is { } live) _ = live.End(LiveEndReason.EndGuestDefeated);
        End(LiveEndReason.EndGuestDefeated);
    }

    /// <summary>The host's "abort": ends the session for both sides; the guest's pawns are reported at once.</summary>
    public static void Abort()
    {
        if (Session is not { } session) return;
        if (RimlinkSession.Live is { } live) _ = live.End(LiveEndReason.EndHostAborted);
        End(LiveEndReason.EndHostAborted);
    }

    /// <summary>The session is over (any reason): the guest's pawns are reported, or the raid degrades to an AI raid when the guest is simply gone.</summary>
    public static void End(LiveEndReason reason)
    {
        if (Session is not { } session) return;
        var map = Map;
        Active = false;
        Starting = false;
        GuestHere = false;
        // The game keeps the speed it runs at (a raid handed to the AI while waiting for the guest stays paused), unless the guest
        // never arrived (refused the host's mods, left while loading): then the waiting pause was all the session did.
        LiveTempo.Reset(restoreSpeed: !guestSeen);
        OrderExecutor.VirtualDrafted.Clear();
        if (RimlinkSession.Live is { } live) _ = live.Close();
        RimlinkLog.Instance.Info($"live session {session.Id} ended: {reason}");
        Messages.Message("Rimlink.Live.Ended".Translate(session.Guest?.Name ?? "?", ("Rimlink.Live.Reason." + reason).Translate()), MessageTypeDefOf.NeutralEvent, historical: true);
        if (map is not null && Find.Maps.Contains(map))
        {
            settled.Add(session.Id); // a map that is gone (a reload) settles nothing: the ended event then reports from the loaded colony
            if (reason == LiveEndReason.EndGuestLost && session.Kind == LiveKind.LiveRaid) Degrade(session, map);
            else Resolve(session, map);
        }
        exits.Clear();
        guestLord = null;
        Session = null;
        Map = null;
        collector = null;
    }

    /// <summary>
    /// The hosted map is gone (this game loaded a save mid-session): the session cannot continue from here. Forgotten without a
    /// report; the caller cancels it on the server and the ended event reports it from the loaded colony.
    /// </summary>
    public static void ForgetIfMapGone()
    {
        if (!Active && !Starting || Map is { } map && Find.Maps.Contains(map)) return;
        RimlinkLog.Instance.Info($"live session {Session?.Id}: the hosted map is gone (a save was loaded); the session is dropped here");
        Active = false;
        Starting = false;
        GuestHere = false;
        Session = null;
        Map = null;
        collector = null;
        guestLord = null;
        exits.Clear();
        LiveTempo.Reset();
        OrderExecutor.VirtualDrafted.Clear();
        if (RimlinkSession.Live is { } live) _ = live.Close();
    }

    /// <summary>A session that ended without this game reporting it: reported now when the home colony is loaded, else at the next load.</summary>
    public static void ReportLater(LiveSession session)
    {
        if (settled.Contains(session.Id)) return;
        if (Verse.Current.ProgramState == ProgramState.Playing && !Visits.VisitSession.InVisit && Find.AnyPlayerHomeMap is not null)
        {
            ResolvePendingEnds([session]);
            return;
        }
        var pending = RimlinkSession.State.Live.PendingEnds;
        if (!pending.Any(s => s.Id == session.Id)) pending.Add(session);
    }

    /// <summary>The guest is gone: their raiders fight on under an AI lord and the 3a resolver reports the outcome later.</summary>
    private static void Degrade(LiveSession session, Map map)
    {
        var owners = MapComponent_LiveOwners.Of(map);
        var pawns = map.mapPawns.AllPawnsSpawned.Where(p => owners.OwnerOf(p) == session.Guest.PlayerId && !p.Dead).ToList();
        var ids = owners.IdsOf(session.Guest.PlayerId);
        foreach (var p in pawns) p.GetLord()?.Notify_PawnLost(p, PawnLostCondition.ForcedToJoinOtherLord);
        guestLord = null;
        var lord = pawns.Count > 0 ? LordMaker.MakeNewLord(RimlinkFactions.Raiders, new LordJob_AssaultColony(RimlinkFactions.Raiders, canKidnap: false, canTimeoutOrFlee: true, sappers: false, useAvoidGridSmart: true, canSteal: true), map, pawns) : null;
        Current.Game.GetComponent<RimlinkGameComponent>().ActiveRaids.Add(new ActiveRaid
        {
            RaidId = session.RaidId,
            LiveSessionId = session.Id,
            AttackerPlayerId = session.Guest.PlayerId,
            AttackerName = session.Guest.Name,
            Lord = lord,
            LandedTick = Find.TickManager.TicksGame,
            PawnIds = ids.Select(id => id ?? 0).ToList(),
            PawnMode = (int)PawnMode.PawnReal,
            LootCap = lootCap,
            AllowKidnap = false,
        });
        owners.ForgetAll(session.Guest.PlayerId);
        RimlinkLog.Instance.Info($"live session {session.Id}: degraded to an AI raid with {pawns.Count} raiders");
        SaveSync.TriggerSave("live-degraded");
    }

    /// <summary>Reports the guest's pawns now (the 3a resolver without its timeout; a visit sends every living pawn home).</summary>
    private static void Resolve(LiveSession session, Map map)
    {
        var owners = MapComponent_LiveOwners.Of(map);
        var ids = owners.IdsOf(session.Guest.PlayerId);
        var raid = new ActiveRaid
        {
            RaidId = session.RaidId,
            LiveSessionId = session.Id,
            Visit = session.Kind == LiveKind.LiveVisit,
            AttackerPlayerId = session.Guest.PlayerId,
            AttackerName = session.Guest.Name,
            Lord = guestLord,
            LandedTick = Find.TickManager.TicksGame,
            PawnIds = ids.Select(id => id ?? 0).ToList(),
            PawnMode = (int)PawnMode.PawnReal,
            LootCap = lootCap,
            AllowKidnap = false,
            Exits = exits.ToList(),
        };
        exits.Clear();
        guestLord = null;
        foreach (var p in map.mapPawns.AllPawnsSpawned.Where(p => owners.OwnerOf(p) == session.Guest.PlayerId && !p.Dead).ToList())
            StripForeignGear(p, owners, raid.Visit);
        owners.ForgetAll(session.Guest.PlayerId);
        RimlinkLog.Instance.Info($"live session {session.Id}: reporting {raid.PawnIds.Count} guest pawns ({string.Join(",", raid.PawnIds)})");
        RaidResolver.ResolveNow(raid, map);
    }

    /// <summary>At login: sessions the server says ended while this host was away still have their pawns on the map.</summary>
    public static void ResolvePendingEnds(IEnumerable<LiveSession> pending)
    {
        foreach (var session in pending)
        {
            var map = Find.Maps.FirstOrDefault(m => m.IsPlayerHome && MapComponent_LiveOwners.Of(m).IdsOf(session.Guest.PlayerId).Count > 0) ?? Find.AnyPlayerHomeMap;
            if (map is null || settled.Contains(session.Id)) continue;
            // Handed to the AI after the guest was lost (saved as an active raid): the 3a resolver reports it when it is over.
            if (Verse.Current.Game?.GetComponent<RimlinkGameComponent>()?.ActiveRaids.Any(r => r.LiveSessionId == session.Id) == true) continue;
            settled.Add(session.Id);
            RimlinkLog.Instance.Info($"live session {session.Id}: reporting its end after the host's return");
            Resolve(session, map);
        }
    }

    /// <summary>
    /// Guest pawns still marked on a home map whose session is neither running nor owed a report (an admin closed it, the
    /// escrow went home): they are copies now and leave the map.
    /// </summary>
    public static void RemoveOrphans(IEnumerable<long> keepOwners)
    {
        var keep = keepOwners.ToHashSet();
        foreach (var map in Find.Maps.Where(m => m.IsPlayerHome))
        {
            var owners = MapComponent_LiveOwners.Of(map);
            foreach (long owner in owners.Owners.Where(o => !keep.Contains(o)).ToList())
            {
                int n = 0;
                foreach (var p in map.mapPawns.AllPawns.Where(p => owners.OwnerOf(p) == owner).ToList())
                {
                    PawnExit.Remove(p);
                    n++;
                }
                owners.ForgetAll(owner);
                RimlinkLog.Instance.Info($"live: removed {n} orphaned guest pawns of player {owner}");
            }
        }
    }

    public static void Reset()
    {
        Active = false;
        Starting = false;
        GuestHere = false;
        Session = null;
        Map = null;
        collector = null;
        guestLord = null;
        exits.Clear();
        settled.Clear(); // session ids are per server; a degraded raid is recognised by its saved active raid, not by this set
        LiveTempo.Reset();
    }

    internal static DeltaCollector? Collector => collector;
}
