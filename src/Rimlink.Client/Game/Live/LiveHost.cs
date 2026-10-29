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

    /// <summary>LiveStarted arrived and this player is the host: fetch the pawns, then spawn and snapshot on the main thread.</summary>
    public static void Begin(LiveSession session)
    {
        if (Active || Starting || !RimlinkSession.Active || RimlinkSession.Live is not { } live || RimlinkSession.Raids is not { } raids) return;
        if (Find.AnyPlayerHomeMap is null || Visits.VisitSession.InVisit) { RimlinkLog.Instance.Warn("live: no home map to host on; aborting"); _ = live.Cancel(session.Id, CancellationToken.None); return; }
        Starting = true;
        Session = session;
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
        var owners = MapComponent_LiveOwners.Of(map);
        var spawned = new List<Pawn>();
        try
        {
            var faction = session.Kind == LiveKind.LiveRaid ? RimlinkFactions.Raiders : Faction.OfPlayer;
            for (int i = 0; i < pawns.Pawns.Count; i++)
            {
                var pawn = ThingBlobs.DeserializePawn(pawns.Pawns[i], newIds: true);
                if (pawn.Faction != faction) pawn.SetFaction(faction);
                owners.Mark(pawn, session.Guest.PlayerId, i);
                spawned.Add(pawn);
            }
            IntVec3 center;
            if (session.Arrival == RaidArrival.ArrivalPods)
            {
                center = DropCellFinder.FindRaidDropCenterDistant(map);
                DropPodUtility.DropThingsNear(center, map, spawned, openDelay: 60, canInstaDropDuringInit: false, leaveSlag: true, canRoofPunch: true, forbid: false);
            }
            else
            {
                if (!RCellFinder.TryFindRandomPawnEntryCell(out center, map, CellFinder.EdgeRoadChance_Hostile)) center = CellFinder.RandomEdgeCell(map);
                foreach (var pawn in spawned) GenSpawn.Spawn(pawn, CellFinder.RandomClosewalkCellNear(center, map, 6), map);
            }
            Find.LetterStack.ReceiveLetter("Rimlink.Live.ArrivedLabel".Translate(session.Guest.Name), "Rimlink.Live.Arrived".Translate(session.Guest.Name, spawned.Count, session.Kind == LiveKind.LiveRaid ? "Rimlink.Live.KindRaid".Translate() : "Rimlink.Live.KindVisit".Translate()),
                session.Kind == LiveKind.LiveRaid ? LetterDefOf.ThreatBig : LetterDefOf.PositiveEvent, new LookTargets(center, map));
        }
        catch (Exception ex)
        {
            Log.Error("[Rimlink] live: the guest's pawns could not be spawned: " + ex);
            foreach (var p in spawned) { if (p.Spawned) p.DeSpawn(); p.Destroy(DestroyMode.Vanish); }
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
            lastDeltaTick = Find.TickManager.TicksGame;
            lastFullTick = lastDeltaTick;
            lastDeltaAt = DateTime.UtcNow;
            _ = Task.Run(async () =>
            {
                try
                {
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
                    LiveTempo.WaitingPause = false;
                    LiveTempo.HostWish = TimeSpeed.Normal;
                    LiveTempo.Apply();
                    Messages.Message("Rimlink.Live.GuestHere".Translate(session.Guest.Name), MessageTypeDefOf.PositiveEvent, historical: false);
                    break;
                case LiveServerMsg.MOneofCase.ResyncRequest:
                    if (!resyncing)
                    {
                        resyncing = true;
                        GuestHere = false; // paused until the guest's new Hello
                        LiveTempo.HostWish = TimeSpeed.Paused;
                        LiveTempo.Apply();
                        Snapshot(live, first: false);
                    }
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
        if (!GuestHere || resyncing) return;
        int tick = Find.TickManager.TicksGame;
        bool paused = Find.TickManager.Paused;
        if (tick - lastDeltaTick < DeltaTicks && !(paused && DateTime.UtcNow - lastDeltaAt > TimeSpan.FromSeconds(1))) return;
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
            if (full)
            {
                var owners = MapComponent_LiveOwners.Of(Map!);
                foreach (var p in Map!.mapPawns.AllPawnsSpawned.Where(owners.IsOwned))
                {
                    try { frames[0].GuestPawnBlobs.Add(ThingBlobs.SerializePawn(p)); } catch { }
                }
                lastFullTick = tick;
            }
            collector.Commit(next);
            foreach (var frame in frames) live.Send(new LiveClientMsg { Delta = frame });
            DeltasSent += frames.Count;
            lastDeltaTick = tick;
            lastDeltaAt = DateTime.UtcNow;
            if (DeltasSent % 100 == 1) RimlinkLog.Instance.Info($"live: sent {DeltasSent} frames (tick {tick}, {next.Pawns.Count} pawns, {next.Things.Count} things)");
        }
        catch (Exception ex)
        {
            Log.Error("[Rimlink] live: delta collection failed: " + ex);
            lastDeltaTick = tick;
        }
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
        LiveTempo.Reset();
        LiveTempo.Apply();
        if (RimlinkSession.Live is { } live) _ = live.Close();
        RimlinkLog.Instance.Info($"live session {session.Id} ended: {reason}");
        Messages.Message("Rimlink.Live.Ended".Translate(session.Guest?.Name ?? "?", reason.ToString()), MessageTypeDefOf.NeutralEvent, historical: true);
        if (map is not null && Find.Maps.Contains(map))
        {
            if (reason == LiveEndReason.EndGuestLost && session.Kind == LiveKind.LiveRaid) Degrade(session, map);
            else Resolve(session, map);
        }
        Session = null;
        Map = null;
        collector = null;
    }

    /// <summary>The guest is gone: their raiders fight on under an AI lord and the 3a resolver reports the outcome later.</summary>
    private static void Degrade(LiveSession session, Map map)
    {
        var owners = MapComponent_LiveOwners.Of(map);
        var pawns = map.mapPawns.AllPawnsSpawned.Where(p => owners.OwnerOf(p) == session.Guest.PlayerId && !p.Dead).ToList();
        var ids = owners.IdsOf(session.Guest.PlayerId);
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
            LandedTick = Find.TickManager.TicksGame,
            PawnIds = ids.Select(id => id ?? 0).ToList(),
            PawnMode = (int)PawnMode.PawnReal,
            LootCap = lootCap,
            AllowKidnap = false,
        };
        owners.ForgetAll(session.Guest.PlayerId);
        RaidResolver.ResolveNow(raid, map);
    }

    /// <summary>At login: sessions the server says ended while this host was away still have their pawns on the map.</summary>
    public static void ResolvePendingEnds(IEnumerable<LiveSession> pending)
    {
        foreach (var session in pending)
        {
            var map = Find.Maps.FirstOrDefault(m => m.IsPlayerHome && MapComponent_LiveOwners.Of(m).IdsOf(session.Guest.PlayerId).Count > 0) ?? Find.AnyPlayerHomeMap;
            if (map is null) continue;
            RimlinkLog.Instance.Info($"live session {session.Id}: reporting its end after the host's return");
            Resolve(session, map);
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
        LiveTempo.Reset();
    }

    internal static DeltaCollector? Collector => collector;
}
