using RimWorld;
using RimWorld.Planet;
using Rimlink.Client.Economy;
using Rimlink.Client.Game.Economy;
using Rimlink.Client.Game.Visits;
using Rimlink.Client.Live;
using Rimlink.Client.UI;
using Rimlink.Client.UI.Live;
using Rimlink.Protocol.V1;
using Verse;
using Verse.AI;
using LiveState = Rimlink.Protocol.V1.LiveState;

namespace Rimlink.Client.Game.Live;

/// <summary>
/// The guest side of a live session (spec §4): proposes with real pawns through the transfer pipeline, waits for the
/// host's answer and snapshot, loads the copy in mirror mode, turns the player's orders into frames and leaves when
/// the session ends. Main thread entry points; the stream lives in RimlinkSession.Live.
/// </summary>
public static class LiveGuest
{
    public static LiveSession? Session { get; private set; }
    public static bool Loading { get; private set; }
    public static int PausesUsed { get; private set; }
    public static bool HostGone { get; private set; }
    public static int UnknownIds { get; private set; }

    private static Dialog_LiveWait? wait;
    private static DateTime nextPoll;
    private static DateTime unknownWindow;
    private static long seq;
    private static readonly Dictionary<long, string> pendingOrders = [];

    private static LiveProposal Build(PlayerRef host, LiveKind kind, PawnList pawns, RaidTiming timing, RaidArrival arrival, long tick) => new()
    {
        ClientRequestId = Guid.NewGuid().ToString("N"),
        HostPlayerId = host.PlayerId,
        Kind = kind,
        Pawns = pawns,
        TimingFallback = timing,
        Arrival = arrival,
        GameTick = tick,
    };

    /// <summary>From a caravan on the host's tile: the chosen pawns leave the caravan for the host's map.</summary>
    public static void ProposeFromCaravan(Caravan caravan, PlayerRef host, LiveKind kind, IReadOnlyList<Pawn> pawns, RaidTiming timing)
    {
        if (!Ready(host, pawns.Count)) return;
        if (kind == LiveKind.LiveRaid && !caravan.PawnsListForReading.Except(pawns).Any(caravan.IsOwner) && caravan.PawnsListForReading.Count > pawns.Count)
        {
            Messages.Message("Rimlink.Economy.KeepOneColonist".Translate(), MessageTypeDefOf.RejectInput, historical: false);
            return;
        }
        if (caravan.PawnsListForReading.Count == pawns.Count) Raids.RaidOutgoing.DisbandForLive(caravan);
        else { Outgoing.RunWithCaravanPawns(caravan, pawns, "Rimlink.Live.Proposed".Translate(host.Name), (list, tick) => Build(host, kind, list, timing, RaidArrival.ArrivalEdge, tick), Call); return; }
        Outgoing.RunWithPodPawns(pawns, "Rimlink.Live.Proposed".Translate(host.Name), (list, tick) => Build(host, kind, list, timing, RaidArrival.ArrivalEdge, tick), Call);
    }

    /// <summary>Dev shortcut and pod arrivals: pawns straight off the map or out of pods.</summary>
    public static void ProposeFromMap(Map map, PlayerRef host, LiveKind kind, IReadOnlyList<Pawn> pawns, RaidTiming timing)
    {
        if (!Ready(host, pawns.Count)) return;
        Outgoing.RunWithMapPawns(map, pawns, "Rimlink.Live.Proposed".Translate(host.Name), (list, tick) => Build(host, kind, list, timing, RaidArrival.ArrivalEdge, tick), Call);
    }

    public static void ProposeFromPods(List<Pawn> pawns, PlayerRef host, LiveKind kind, RaidTiming timing)
    {
        if (!Ready(host, pawns.Count)) { Raids.RaidOutgoing.ReturnPodPawnsHome(pawns); return; }
        Outgoing.RunWithPodPawns(pawns, "Rimlink.Live.Proposed".Translate(host.Name), (list, tick) => Build(host, kind, list, timing, RaidArrival.ArrivalPods, tick), Call);
    }

    private static bool Ready(PlayerRef host, int pawns)
    {
        if (!RimlinkSession.Active || RimlinkSession.Live is null) return false;
        if (Session is not null || MirrorMode.Active || VisitSession.InVisit || VisitSession.Starting) { Messages.Message("Rimlink.Live.Busy".Translate(), MessageTypeDefOf.RejectInput, historical: false); return false; }
        if (Raids.RaidOutgoing.CheckLimits(pawns) is { } limit) { Messages.Message(limit, MessageTypeDefOf.RejectInput, historical: false); return false; }
        return true;
    }

    private static async Task Call(Google.Protobuf.IMessage request)
    {
        var session = await RimlinkSession.Live!.Propose((LiveProposal)request, CancellationToken.None);
        MainThread.Post(() => OnProposed(session));
    }

    private static void OnProposed(LiveSession session)
    {
        Session = session;
        if (session.State == LiveState.LiveProposed)
        {
            wait = new Dialog_LiveWait(session);
            Find.WindowStack.Add(wait);
        }
        else if (session.State == LiveState.LiveStarting) OnStarted(session);
    }

    /// <summary>The host accepted (or auto-accepts): wait for their snapshot, then load it.</summary>
    public static void OnStarted(LiveSession session)
    {
        if (Session is not null && Session.Id != session.Id) return;
        Session = session;
        LiveTempo.UseBudget(session); // the HUD shows the budget of this session
        wait?.Close();
        wait = null;
        nextPoll = DateTime.UtcNow;
        Messages.Message("Rimlink.Live.Accepted".Translate(session.Host?.Name ?? "?"), MessageTypeDefOf.PositiveEvent, historical: false);
    }

    public static void OnFallback(LiveSession session)
    {
        if (Session?.Id != session.Id) return;
        wait?.Close();
        wait = null;
        Session = null;
        Messages.Message((session.Kind == LiveKind.LiveRaid ? "Rimlink.Live.FellBackRaid" : "Rimlink.Live.FellBackVisit").Translate(session.Host?.Name ?? "?"), MessageTypeDefOf.NeutralEvent, historical: true);
    }

    /// <summary>LiveEnded from the server (admin end, host lost, host abort) while we are not yet, or no longer, on the stream.</summary>
    public static void OnEnded(LiveSession session)
    {
        if (Session?.Id != session.Id) return;
        if (MirrorMode.Active) { Exit(session.EndReason); return; }
        wait?.Close();
        wait = null;
        Session = null;
        Loading = false;
        Messages.Message(EndedText(session.Host?.Name ?? "?", session.EndReason), MessageTypeDefOf.NeutralEvent, historical: true);
    }

    public static void CancelProposal()
    {
        if (Session is not { } session || RimlinkSession.Live is not { } live) return;
        wait?.Close();
        wait = null;
        Session = null;
        _ = live.Cancel(session.Id, CancellationToken.None);
    }

    /// <summary>Called every frame by the game component.</summary>
    public static void Tick()
    {
        ShowEndedNotice();
        if (Session is not { } session || RimlinkSession.Live is not { } live) return;
        if (!MirrorMode.Active)
        {
            if (Loading || session.State is not (LiveState.LiveStarting or LiveState.LiveRunning or LiveState.LiveWaiting) || DateTime.UtcNow < nextPoll) return;
            nextPoll = DateTime.UtcNow + TimeSpan.FromSeconds(1);
            _ = Task.Run(async () =>
            {
                try
                {
                    var current = await live.Current(CancellationToken.None);
                    MainThread.Post(() =>
                    {
                        if (Session?.Id != current.Id || Loading || MirrorMode.Active) return;
                        Session = current;
                        if (current.SnapshotVersion > 0) LoadSnapshot(current);
                    });
                }
                catch (Grpc.Core.RpcException ex) when (EconomyClient.CodeOf(ex) == ErrorCode.LiveGone)
                {
                    MainThread.Post(() => { if (Session?.Id == session.Id && !MirrorMode.Active) OnEnded(session); });
                }
                catch (Exception ex) { RimlinkLog.Instance.Warn("live: session state not fetched: " + ex.Message); }
            });
            return;
        }
        MirrorMode.ClaimOwnPawns();
        // A slow frame (the game in the background, a host at full speed) must not let the queue overflow: past the first 64
        // frames the drain goes on for a few milliseconds more.
        var drainUntil = DateTime.UtcNow + TimeSpan.FromMilliseconds(8);
        for (int i = 0; (i < 64 || DateTime.UtcNow < drainUntil) && live.Incoming.TryDequeue(out var frame); i++)
        {
            switch (frame!.MCase)
            {
                case LiveServerMsg.MOneofCase.Delta:
                    CountUnknown(MirrorApplier.Apply(frame.Delta));
                    break;
                case LiveServerMsg.MOneofCase.OrderResult:
                    if (pendingOrders.Remove(frame.OrderResult.Seq, out string? kind))
                    {
                        if (!frame.OrderResult.Ok) Messages.Message("Rimlink.Live.OrderRefused".Translate(kind, frame.OrderResult.Reason), MessageTypeDefOf.RejectInput, historical: false);
                        else if (kind == "pause") PausesUsed++;
                    }
                    break;
                case LiveServerMsg.MOneofCase.SnapshotReady:
                    if (frame.SnapshotReady.SaveVersion > MirrorMode.SnapshotVersion && !Loading) { Session.SnapshotVersion = frame.SnapshotReady.SaveVersion; LoadSnapshot(Session); return; }
                    break;
                case LiveServerMsg.MOneofCase.PeerLost:
                    HostGone = true;
                    MirrorMode.SetLocalSpeed(TimeSpeed.Paused);
                    Messages.Message("Rimlink.Live.HostLost".Translate(session.Host?.Name ?? "?"), MessageTypeDefOf.NegativeEvent, historical: false);
                    break;
                case LiveServerMsg.MOneofCase.PeerBack:
                    HostGone = false;
                    Messages.Message("Rimlink.Live.HostBack".Translate(session.Host?.Name ?? "?"), MessageTypeDefOf.PositiveEvent, historical: false);
                    break;
                case LiveServerMsg.MOneofCase.End:
                    Exit(frame.End.Reason);
                    return;
                case LiveServerMsg.MOneofCase.Notice:
                    RimlinkLog.Instance.Warn("live: " + frame.Notice.Text);
                    break;
            }
        }
        // A dropped delta is a change this mirror never saw (a pawn carried off stays where it lay): only a snapshot repairs it.
        if (live.Incoming.TakeDroppedDeltas() is > 0 and int dropped && !Loading)
            live.RequestResync($"{dropped} deltas dropped while the mirror fell behind");
    }

    /// <summary>The stream is over for good (peer End, replaced, or the reconnect budget spent): raised on a background thread.</summary>
    private static void OnStreamEnded(LiveEndReason reason) => MainThread.Post(() => { if (Session is not null && MirrorMode.Active) Exit(reason); });

    private static void CountUnknown(int unknown)
    {
        if (unknown == 0) return;
        if (DateTime.UtcNow > unknownWindow) { unknownWindow = DateTime.UtcNow + TimeSpan.FromMinutes(1); UnknownIds = 0; }
        UnknownIds += unknown;
        if (UnknownIds > 100 && RimlinkSession.Live is { } live && !Loading)
        {
            UnknownIds = 0;
            live.RequestResync("too many unknown ids in deltas");
        }
    }

    /// <summary>Downloads the host's snapshot and loads it as a visit copy in mirror mode (also for a resync).</summary>
    private static void LoadSnapshot(LiveSession session)
    {
        Loading = true;
        if (MirrorMode.Active) MirrorMode.Exit();
        RimlinkLog.Instance.Info($"live session {session.Id}: loading {session.Host?.Name}'s snapshot v{session.SnapshotVersion}");
        VisitSession.BeginLive(session, () => { Loading = false; });
    }

    /// <summary>The copy loaded: enter mirror mode and announce ourselves on the stream (the host unpauses on our Hello).</summary>
    public static void OnMirrorLoaded(LiveSession session)
    {
        Loading = false;
        if (Session?.Id != session.Id || RimlinkSession.Live is not { } live) return;
        try
        {
            MirrorMode.Enter(session, session.SnapshotVersion);
        }
        catch (Exception ex)
        {
            Log.Error("[Rimlink] mirror could not start: " + ex);
            Abort("mirror: " + ex.Message);
            return;
        }
        HostGone = false;
        live.Ended -= OnStreamEnded;
        live.Ended += OnStreamEnded;
        _ = Task.Run(async () =>
        {
            try { await live.Open(session.Id, LiveClient.GuestRole, session.SnapshotVersion, CancellationToken.None); }
            catch (Exception ex) { RimlinkLog.Instance.Warn("live: the stream could not be opened: " + ex.Message); MainThread.Post(() => Abort(ex.Message)); }
        });
        Messages.Message("Rimlink.Live.MirrorReady".Translate(session.Host?.Name ?? "?"), MessageTypeDefOf.PositiveEvent, historical: false);
    }

    /// <summary>Something on our side failed: tell the host and leave.</summary>
    public static void Abort(string reason)
    {
        RimlinkLog.Instance.Warn("live: aborting: " + reason);
        TellTheServer(LiveEndReason.EndGuestCancelled);
        Exit(LiveEndReason.EndGuestCancelled);
    }

    /// <summary>The player's "End session" button.</summary>
    public static void EndByPlayer()
    {
        TellTheServer(LiveEndReason.EndNormal);
        Exit(LiveEndReason.EndNormal);
    }

    /// <summary>The End frame when the stream is open; before that (the snapshot never loaded) the unary Cancel, so the host does not wait for a guest who left.</summary>
    private static void TellTheServer(LiveEndReason reason)
    {
        if (RimlinkSession.Live is not { } live || Session is not { } session) return;
        if (live.IsOpen) _ = live.End(reason);
        else _ = Task.Run(async () => { try { await live.Cancel(session.Id, CancellationToken.None); } catch (Exception ex) { RimlinkLog.Instance.Warn("live: cancel not delivered: " + ex.Message); } });
    }

    private static void Exit(LiveEndReason reason)
    {
        var session = Session;
        Session = null;
        Loading = false;
        HostGone = false;
        PausesUsed = 0;
        pendingOrders.Clear();
        wait?.Close();
        wait = null;
        if (RimlinkSession.Live is { } live) { live.Ended -= OnStreamEnded; _ = live.Close(); }
        MirrorMode.Exit();
        if (session is not null)
        {
            string text = EndedText(session.Host?.Name ?? "?", reason);
            // Inside the host's copy a message would go with the copy: it is shown once our own colony has loaded.
            if (VisitSession.Active) endedNotice = (text, Verse.Current.Game);
            else Messages.Message(text, MessageTypeDefOf.NeutralEvent, historical: true);
        }
        if (VisitSession.Active) VisitSession.End(); // also after a failed resync: we are still inside the host's copy
    }

    private static (string Text, Verse.Game? ShownIn)? endedNotice;

    /// <summary>The ended session's line, on the first frame of play in another game than the copy it ended in.</summary>
    private static void ShowEndedNotice()
    {
        if (endedNotice is not { } notice || Verse.Current.Game == notice.ShownIn || Verse.Current.ProgramState != ProgramState.Playing || VisitSession.InVisit) return;
        endedNotice = null;
        Messages.Message(notice.Text, MessageTypeDefOf.NeutralEvent, historical: true);
    }

    /// <summary>What the guest reads when the session is over: its own line when the host ended it for lack of guest pawns.</summary>
    private static string EndedText(string host, LiveEndReason reason) =>
        reason == LiveEndReason.EndGuestDefeated
            ? "Rimlink.Live.EndedDefeated".Translate(host)
            : "Rimlink.Live.EndedGuest".Translate(host, reason.ToString());

    public static void Reset()
    {
        Session = null;
        Loading = false;
        HostGone = false;
        PausesUsed = 0;
        pendingOrders.Clear();
        wait = null;
        MirrorMode.Exit();
    }

    private static void Send(Order order, string kind)
    {
        if (RimlinkSession.Live is not { } live) return;
        order.Seq = ++seq;
        pendingOrders[order.Seq] = kind;
        if (pendingOrders.Count > 200) pendingOrders.Remove(pendingOrders.Keys.Min());
        live.Send(new LiveClientMsg { Order = order });
    }

    /// <summary>A vanilla ordered job on one of our pawns becomes an order frame (Patch_Live_Mirror).</summary>
    public static void SendJob(Pawn pawn, Job job)
    {
        if (job.def == JobDefOf.Goto && job.targetA.IsValid && !job.targetA.HasThing)
            Send(new Order { PawnId = pawn.thingIDNumber, MoveTo = new Cell { X = job.targetA.Cell.x, Z = job.targetA.Cell.z } }, "move");
        else if (job.def == JobDefOf.AttackMelee || job.def == JobDefOf.AttackStatic)
            Send(new Order { PawnId = pawn.thingIDNumber, Attack = TargetOf(job.targetA) }, "attack");
        else if (job.def == JobDefOf.Equip && job.targetA.HasThing) Send(new Order { PawnId = pawn.thingIDNumber, Equip = job.targetA.Thing.thingIDNumber }, "equip");
        else if (job.def == JobDefOf.Wear && job.targetA.HasThing) Send(new Order { PawnId = pawn.thingIDNumber, Wear = job.targetA.Thing.thingIDNumber }, "wear");
        else if ((job.def == JobDefOf.DropEquipment || job.def == JobDefOf.RemoveApparel) && job.targetA.HasThing) Send(new Order { PawnId = pawn.thingIDNumber, Drop = job.targetA.Thing.thingIDNumber }, "drop");
        else
        {
            var take = new TakeJob { JobDef = job.def.defName, Count = job.count, Force = job.playerForced };
            if (job.targetA.IsValid) take.A = TargetOf(job.targetA);
            if (job.targetB.IsValid) take.B = TargetOf(job.targetB);
            if (job.targetC.IsValid) take.C = TargetOf(job.targetC);
            Send(new Order { PawnId = pawn.thingIDNumber, TakeJob = take }, job.def.defName);
        }
    }

    public static void SendDraft(Pawn pawn, bool drafted) => Send(new Order { PawnId = pawn.thingIDNumber, Draft = drafted }, "draft");

    public static void SendWorkPriority(Pawn pawn, WorkTypeDef work, int priority) => Send(new Order { PawnId = pawn.thingIDNumber, WorkPriority = new WorkPriority { WorkDef = work.defName, Priority = priority } }, "work priority");

    public static void SendSchedule(Pawn pawn, int hour, TimeAssignmentDef assignment) => Send(new Order { PawnId = pawn.thingIDNumber, Schedule = new Schedule { Hour = hour, AssignmentDef = assignment.defName } }, "schedule");

    /// <summary>The speed buttons: a pause asks the host (budgeted), a speed is the guest's wish (the host runs at the slower of the two).</summary>
    /// <summary>The pause button or Space in the mirror: lifts our own pause or asks for one; a pause the host set is theirs to lift.</summary>
    public static void TogglePause()
    {
        if (MirrorMode.PausedBy == 2) Send(new Order { Pause = false }, "unpause");
        else if (MirrorMode.PausedBy == 0) Send(new Order { Pause = true }, "pause");
    }

    public static void SendSpeed(TimeSpeed speed)
    {
        if (speed == TimeSpeed.Paused) Send(new Order { Pause = true }, "pause");
        else
        {
            if (MirrorMode.PausedBy == 2) Send(new Order { Pause = false }, "unpause");
            Send(new Order { SetSpeed = (int)speed }, "speed");
        }
    }

    private static Target TargetOf(LocalTargetInfo t) => t.HasThing ? new Target { ThingId = t.Thing.thingIDNumber } : new Target { Cell = new Cell { X = t.Cell.x, Z = t.Cell.z } };
}
