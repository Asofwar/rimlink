using RimWorld;
using Rimlink.Client.Net;
using Rimlink.Protocol.V1;
using Verse;

namespace Rimlink.Client.Game;

/// <summary>Main-thread pump: drains the event inbox once per frame and reacts to connection state. Does nothing in single player.</summary>
public sealed class RimlinkGameComponent : GameComponent
{
    private ConnectionState shownState = ConnectionState.Connected;
    private static Dialog_MessageBox? outageOffer;

    /// <summary>Raids landed on this colony and not yet reported; saved with the game so a reload still resolves them.</summary>
    public List<Raids.ActiveRaid> ActiveRaids = [];

    public RimlinkGameComponent(Verse.Game game) { }

    public override void ExposeData()
    {
        base.ExposeData();
        Scribe_Collections.Look(ref ActiveRaids, "rimlinkActiveRaids", LookMode.Deep);
        ActiveRaids ??= [];
    }

    public override void FinalizeInit()
    {
        if (!RimlinkSession.Active) return;
        ForeignSettlements.Subscribe();
        ForeignSettlements.RebuildFromState();
        SubscribeLive();
        Economy.SaleNotices.Subscribe();
        // A visit's game is the host's colony: removing "sold" things there would strip the guest's copy, not the home map.
        if (!Visits.VisitSession.InVisit) Shop.ShopSync.ApplyRemovals();
        if (!Visits.VisitSession.InVisit) SettleLiveSessionsAfterLoad();
    }

    /// <summary>
    /// After a load of the home colony: sessions that ended while this host was away are reported (their guest pawns are still
    /// on the map), a session this host was in the middle of cannot be resumed from a save and is cancelled (the server then
    /// reports it as ended and the pawns are reported the same way), and guest pawns of any other session are copies to remove.
    /// </summary>
    private static void SettleLiveSessionsAfterLoad()
    {
        Live.LiveHost.ForgetIfMapGone(); // a save loaded mid-session: the session's map is gone, the loaded colony holds the guest pawns
        var state = RimlinkSession.State.Live;
        var keep = new List<long>();
        if (state.PendingEnds.Count > 0)
        {
            var pending = state.PendingEnds.ToList();
            state.PendingEnds.Clear();
            Live.LiveHost.ResolvePendingEnds(pending);
        }
        if (state.Current is { } current && current.Host?.PlayerId == RimlinkSession.MyPlayerId && !Live.LiveHost.Active && RimlinkSession.Live is { } live)
        {
            keep.Add(current.Guest.PlayerId);
            RimlinkLog.Instance.Info($"live session {current.Id}: this host reloaded its game; the session cannot continue and is cancelled");
            _ = Task.Run(async () => { try { await live.Cancel(current.Id, CancellationToken.None); } catch (Exception ex) { RimlinkLog.Instance.Warn("live: cancel not delivered: " + ex.Message); } });
        }
        Live.LiveHost.RemoveOrphans(keep);
    }

    private static bool liveSubscribed;
    private static bool? atHomeSent;

    private static void SubscribeLive()
    {
        if (liveSubscribed) return;
        liveSubscribed = true;
        var live = RimlinkSession.State.Live;
        live.Invited += invite => { if (Current.ProgramState == ProgramState.Playing && !Visits.VisitSession.InVisit) Find.WindowStack.Add(new UI.Live.Dialog_LiveInvite(invite)); };
        live.Started += session =>
        {
            if (session.Host?.PlayerId == RimlinkSession.MyPlayerId) Live.LiveHost.Begin(session);
            else if (session.Guest?.PlayerId == RimlinkSession.MyPlayerId) Live.LiveGuest.OnStarted(session);
        };
        live.Ended += session =>
        {
            if (session.Host?.PlayerId == RimlinkSession.MyPlayerId && session.State == LiveState.LiveEnding)
            {
                if (Live.LiveHost.Session?.Id == session.Id) Live.LiveHost.End(session.EndReason);
                // Not settled by that (the hosted map was gone) or ended while this game was not hosting it: the pawns are in the home colony.
                if (!Live.LiveHost.Active) Live.LiveHost.ReportLater(session);
            }
            else if (session.Guest?.PlayerId == RimlinkSession.MyPlayerId && session.State == LiveState.LiveEnding) Live.LiveGuest.OnEnded(session);
        };
        live.FellBack += session => { if (session.Guest?.PlayerId == RimlinkSession.MyPlayerId) Live.LiveGuest.OnFallback(session); };
    }

    /// <summary>Presence for live sessions: the server only invites a host who is at their colony.</summary>
    private static void ReportPresence()
    {
        bool home = Current.ProgramState == ProgramState.Playing && !Visits.VisitSession.InVisit && !Live.MirrorMode.Active && Find.AnyPlayerHomeMap is not null;
        if (atHomeSent == home || RimlinkSession.Connection is not { } connection) return;
        atHomeSent = home;
        _ = Task.Run(async () =>
        {
            try { await connection.Auth.SetAtHomeAsync(new AtHomeRequest { AtHome = home }); }
            catch (Exception ex) { RimlinkLog.Instance.Warn("presence not reported: " + ex.Message); atHomeSent = null; }
        });
    }

    public override void GameComponentOnGUI() => UI.Overlay.Draw();

    private static string? afterLoadNotice;
    private static Verse.Game? noticeSetIn;

    /// <summary>
    /// A message for the game being loaded: loading disposes this game and reloads the scene, so it is shown on the first
    /// frame of the next game (which runs without a session, hence before the session check below).
    /// </summary>
    public static void NoticeAfterLoad(string key)
    {
        afterLoadNotice = key;
        noticeSetIn = Current.Game;
    }

    private static void ShowNoticeAfterLoad()
    {
        if (afterLoadNotice is not { } key || Current.Game == noticeSetIn || Current.ProgramState != ProgramState.Playing) return;
        afterLoadNotice = null;
        noticeSetIn = null;
        // The load failed (back to the menu) and this is a later game: a new session says nothing about the ended one.
        if (RimlinkSession.Active) return;
        Find.WindowStack.Add(new Dialog_MessageBox(key.Translate()));
    }

    public override void GameComponentUpdate()
    {
        ShowNoticeAfterLoad();
        if (!RimlinkSession.Active) return;

        RimlinkSession.Inbox.DrainTo(RimlinkSession.State.Apply);
        HandleConnectionState();
        HandleNotices();
        ReportPresence();
        Economy.Incoming.Tick();
        Raids.RaidIncoming.Tick();
        Raids.RaidResolver.Tick();
        Live.LiveHost.Tick();
        Live.LiveGuest.Tick();
        HandleStaleSave();
    }

    private void HandleConnectionState()
    {
        OfferExitOnLongOutage();
        var state = RimlinkSession.LastState;
        if (state == shownState) return;
        shownState = state;
        if (state != ConnectionState.Reconnecting) CloseOutageOffer();

        switch (state)
        {
            case ConnectionState.Reconnecting:
                Find.TickManager.Pause();
                Messages.Message("Rimlink.Net.Reconnecting".Translate(), MessageTypeDefOf.NegativeEvent, historical: false);
                break;
            case ConnectionState.Connected:
                Messages.Message("Rimlink.Net.Reconnected".Translate(), MessageTypeDefOf.PositiveEvent, historical: false);
                atHomeSent = null; // the new server session starts away from home: say where we are again
                Resync();
                break;
            case ConnectionState.Replaced:
                Find.TickManager.Pause();
                LeaveReplaced();
                Find.WindowStack.Add(new Dialog_MessageBox("Rimlink.Net.Replaced".Translate(), "Rimlink.Net.ToMenu".Translate(), GenScene.GoToMainMenu));
                break;
            case ConnectionState.Failed:
                Find.TickManager.Pause();
                Find.WindowStack.Add(new Dialog_MessageBox("Rimlink.Net.Failed".Translate(RimlinkSession.Connection?.FailureReason ?? "?"), "Rimlink.Net.ToMenu".Translate(), LeaveToMenu));
                break;
        }
    }

    /// <summary>
    /// Two minutes without the server (core spec §9): offer leaving to the menu with a local save, once per outage. The
    /// attempts go on behind the offer, which closes itself when the connection is back. A live guest's mirror waits for the
    /// live session instead (its own budget ends the session and loads the own colony back), so no offer while it is up.
    /// </summary>
    private static void OfferExitOnLongOutage()
    {
        if (outageOffer is not null || Live.MirrorMode.Active || RimlinkSession.Connection is not { OutageIsLong: true }) return;
        outageOffer = new Dialog_MessageBox("Rimlink.Net.LongOutage".Translate(), "Rimlink.Net.KeepWaiting".Translate(), null, "Rimlink.Net.ToMenu".Translate(), LeaveToMenu);
        Find.WindowStack.Add(outageOffer);
    }

    private static void CloseOutageOffer()
    {
        if (outageOffer is { } offer && Find.WindowStack.IsOpen(offer)) offer.Close(doCloseSound: false);
        outageOffer = null;
    }

    /// <summary>A reconnect starts a fresh server session with an empty event queue: fetch what was missed meanwhile.</summary>
    private static void Resync()
    {
        var connection = RimlinkSession.Connection;
        var login = RimlinkSession.Login;
        if (connection is null || login is null) return;
        _ = Task.Run(async () =>
        {
            try
            {
                var objects = await connection.World.ListObjectsAsync(new Empty());
                var history = await connection.Chat.HistoryAsync(new Empty());
                var online = connection.Login?.Online.ToList() ?? [login.Player];
                UI.MainThread.Post(() =>
                {
                    if (RimlinkSession.Connection != connection) return;
                    RimlinkSession.State.LoadSnapshot(objects, history, online);
                    ForeignSettlements.RebuildFromState();
                    RimlinkLog.Instance.Info($"resynced after reconnect: {objects.Objects.Count} world objects, {online.Count} online");
                });
                await UI.LoginFlow.LoadEconomy(connection, RimlinkSession.Outbox, login.Player.PlayerId, CancellationToken.None);
            }
            catch (Exception ex)
            {
                RimlinkLog.Instance.Warn($"resync after reconnect failed: {ex.Message}");
            }
        });
    }

    private void HandleNotices()
    {
        var state = RimlinkSession.State;
        foreach (var notice in state.Notices) Messages.Message(notice.Text, MessageTypeDefOf.NeutralEvent, historical: false);
        state.Notices.Clear();

        if (state.PendingShutdown is { } shutdown)
        {
            Messages.Message("Rimlink.Net.Shutdown".Translate(shutdown.InSeconds, shutdown.Reason), MessageTypeDefOf.ThreatBig, historical: false);
            if (shutdown.InSeconds > 10) SaveSync.TriggerSave("server shutdown");
            state.ClearPendingShutdown();
        }

        if (state.PendingRollbackVersion is { } version)
        {
            state.ClearPendingRollback();
            Find.TickManager.Pause();
            Find.WindowStack.Add(new Dialog_MessageBox("Rimlink.Save.RolledBack".Translate(version), "Rimlink.Net.ToMenu".Translate(), LeaveToMenu));
        }
    }

    private static void HandleStaleSave()
    {
        if (!SaveSync.StaleSaveDetected) return;
        SaveSync.StaleSaveDetected = false;
        Find.TickManager.Pause();
        var box = new Dialog_MessageBox("Rimlink.Save.Stale".Translate(), "Rimlink.Net.ToMenu".Translate(), AbortToMenu, "Rimlink.Save.KeepPlaying".Translate(), null);
        box.forcePause = true;
        Find.WindowStack.Add(box);
    }

    /// <summary>The colony is saved locally and flagged for upload at the next login (the connection is gone); the session ends.</summary>
    /// <summary>
    /// The newer login is the colony now: this copy must never reach the server, not even as a pending upload at the next
    /// login from here (it would overwrite what was played there). The session ends at once, so leaving saves nothing.
    /// </summary>
    private static void LeaveReplaced()
    {
        SaveSync.Pending.Clear();
        EndSession();
    }

    public static void LeaveToMenu()
    {
        SaveSync.SaveLocallyForLater("leaving after a lost or replaced session");
        EndSession();
        GenScene.GoToMainMenu();
    }

    /// <summary>The server refused what the game did (a settlement on a taken tile): the colony cannot continue here.</summary>
    public static void AbortToMenu()
    {
        EndSession();
        GenScene.GoToMainMenu();
    }

    public static void EndSession()
    {
        var connection = RimlinkSession.Connection;
        // First, and waited for: a ticket End sent after the logout below arrives with an expired session and leaves the
        // ticket open for Raids:VisitHours (no visit or live session for this player meanwhile).
        var visitEnd = Visits.VisitSession.Reset();
        try { visitEnd?.Wait(TimeSpan.FromSeconds(3)); }
        catch (AggregateException ex) { RimlinkLog.Instance.Warn("visit ticket not closed: " + ex.InnerException?.Message); }
        Live.LiveHost.Reset();
        Live.LiveGuest.Reset();
        atHomeSent = null;
        RimlinkSession.End();
        ForeignSettlements.Clear();
        Economy.Incoming.Reset();
        Raids.RaidIncoming.Reset();
        if (connection is not null) _ = connection.DisposeAsync();
    }
}
