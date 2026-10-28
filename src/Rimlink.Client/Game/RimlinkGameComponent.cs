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
        Shop.ShopSync.ApplyRemovals();
    }

    public override void GameComponentOnGUI() => UI.Overlay.Draw();

    public override void GameComponentUpdate()
    {
        if (!RimlinkSession.Active) return;

        RimlinkSession.Inbox.DrainTo(RimlinkSession.State.Apply);
        HandleConnectionState();
        HandleNotices();
        Economy.Incoming.Tick();
        Raids.RaidIncoming.Tick();
        Raids.RaidResolver.Tick();
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
        RimlinkSession.End();
        ForeignSettlements.Clear();
        Economy.Incoming.Reset();
        Raids.RaidIncoming.Reset();
        Visits.VisitSession.Reset();
        if (connection is not null) _ = connection.DisposeAsync();
    }
}
