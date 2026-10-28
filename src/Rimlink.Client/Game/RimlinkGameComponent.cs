using RimWorld;
using Rimlink.Client.Net;
using Verse;

namespace Rimlink.Client.Game;

/// <summary>Main-thread pump: drains the event inbox once per frame and reacts to connection state. Does nothing in single player.</summary>
public sealed class RimlinkGameComponent : GameComponent
{
    private ConnectionState shownState = ConnectionState.Connected;

    public RimlinkGameComponent(Verse.Game game) { }

    public override void FinalizeInit()
    {
        if (!RimlinkSession.Active) return;
        ForeignSettlements.Subscribe();
        ForeignSettlements.RebuildFromState();
    }

    public override void GameComponentOnGUI() => UI.Overlay.Draw();

    public override void GameComponentUpdate()
    {
        if (!RimlinkSession.Active) return;

        RimlinkSession.Inbox.DrainTo(RimlinkSession.State.Apply);
        HandleConnectionState();
        HandleNotices();
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

    /// <summary>Local save stays as it is (its upload is retried at next login); the session ends.</summary>
    public static void LeaveToMenu()
    {
        var connection = RimlinkSession.Connection;
        RimlinkSession.End();
        ForeignSettlements.Clear();
        if (connection is not null) _ = connection.DisposeAsync();
        GenScene.GoToMainMenu();
    }
}
