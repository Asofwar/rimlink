using RimWorld;
using Rimlink.Client.Net;
using Rimlink.Protocol.V1;
using Verse;

namespace Rimlink.Client.Game;

/// <summary>Main-thread pump: drains the event inbox once per frame and reacts to connection state. Does nothing in single player.</summary>
public sealed class RimlinkGameComponent : GameComponent
{
    private ConnectionState shownState = ConnectionState.Connected;

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
        // A visit's game is the host's colony: removing "sold" things there would strip the guest's copy, not the home map.
        if (!Visits.VisitSession.InVisit) Shop.ShopSync.ApplyRemovals();
        // Live sessions that ended while this host was away: their guest pawns are still on the map and owed a report.
        if (!Visits.VisitSession.InVisit && RimlinkSession.State.Live.PendingEnds.Count > 0)
        {
            var pending = RimlinkSession.State.Live.PendingEnds.ToList();
            RimlinkSession.State.Live.PendingEnds.Clear();
            Live.LiveHost.ResolvePendingEnds(pending);
        }
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
            if (session.Host?.PlayerId == RimlinkSession.MyPlayerId && Live.LiveHost.Session?.Id == session.Id && session.State == LiveState.LiveEnding) Live.LiveHost.End(session.EndReason);
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

    public override void GameComponentUpdate()
    {
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
        var state = RimlinkSession.LastState;
        if (state == shownState) return;
        shownState = state;

        switch (state)
        {
            case ConnectionState.Reconnecting:
                Find.TickManager.Pause();
                Messages.Message("Rimlink.Net.Reconnecting".Translate(), MessageTypeDefOf.NegativeEvent, historical: false);
                break;
            case ConnectionState.Connected:
                Messages.Message("Rimlink.Net.Reconnected".Translate(), MessageTypeDefOf.PositiveEvent, historical: false);
                Resync();
                break;
            case ConnectionState.Replaced:
                Find.TickManager.Pause();
                Find.WindowStack.Add(new Dialog_MessageBox("Rimlink.Net.Replaced".Translate(), "Rimlink.Net.ToMenu".Translate(), LeaveToMenu));
                break;
            case ConnectionState.Failed:
                Find.TickManager.Pause();
                Find.WindowStack.Add(new Dialog_MessageBox("Rimlink.Net.Failed".Translate(), "Rimlink.Net.ToMenu".Translate(), LeaveToMenu));
                break;
        }
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
        Visits.VisitSession.Reset(); // first: it still needs the connection to close an open ticket
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
