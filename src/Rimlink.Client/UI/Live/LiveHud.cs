using Rimlink.Client.Game.Live;
using Rimlink.Protocol.V1;
using UnityEngine;
using Verse;

namespace Rimlink.Client.UI.Live;

/// <summary>The host's corner of the overlay during a live session: who is here, the tempo, and the abort button.</summary>
public static class LiveHud
{
    /// <summary>The guest's corner: whose map, the host's tempo, the pause budget and the end button.</summary>
    public static float DrawGuest(float x, float y)
    {
        var session = MirrorMode.Session;
        if (session is null) return 0f;
        Text.Font = GameFont.Small;
        string kind = session.Kind == LiveKind.LiveRaid ? "Rimlink.Live.KindRaid".Translate() : "Rimlink.Live.KindVisit".Translate();
        Widgets.Label(new Rect(x, y, 220f, 24f), "Rimlink.Live.GuestHud".Translate(kind, session.Host?.Name ?? "?"));
        Text.Font = GameFont.Tiny;
        string state = LiveGuest.HostGone ? "Rimlink.Live.StateHostLost".Translate()
            : MirrorMode.PausedBy == 3 ? "Rimlink.Live.StateLetterPaused".Translate(MirrorMode.LetterLabel ?? "?")
            : MirrorMode.PausedBy == 1 ? "Rimlink.Live.StateHostPaused".Translate()
            : MirrorMode.PausedBy == 2 ? "Rimlink.Live.StateGuestPaused".Translate(LiveGuest.PausesUsed, LiveTempo.GuestPauses)
            : "Rimlink.Live.StateTick".Translate(MirrorMode.TargetTick, LiveGuest.PausesUsed, LiveTempo.GuestPauses);
        Widgets.Label(new Rect(x, y + 22f, 220f, 20f), $"<color=#9aa4b5>{state}</color>");
        Text.Font = GameFont.Small;
        if (Widgets.ButtonText(new Rect(x, y + 44f, 220f, 26f), "Rimlink.Live.EndSession".Translate()))
            Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation("Rimlink.Live.EndSessionConfirm".Translate(), LiveGuest.EndByPlayer, destructive: true));
        return 76f;
    }

    /// <summary>Draws at (x, y) and returns the height used.</summary>
    public static float DrawHost(float x, float y)
    {
        if (!LiveHost.Active && !LiveHost.Starting) return 0f;
        Text.Font = GameFont.Small;
        string kind = LiveHost.Session?.Kind == LiveKind.LiveRaid ? "Rimlink.Live.KindRaid".Translate() : "Rimlink.Live.KindVisit".Translate();
        string state = LiveHost.Starting ? "Rimlink.Live.StateStarting".Translate()
            : !LiveHost.GuestHere ? "Rimlink.Live.StateWaiting".Translate()
            : LiveTempo.PausedBy == 3 ? "Rimlink.Live.StateLetterPaused".Translate(LiveTempo.Rules.LetterLabel ?? "?")
            : LiveTempo.PausedBy == 2 ? "Rimlink.Live.StateGuestPaused".Translate(LiveTempo.GuestPausesUsed, LiveTempo.GuestPauses)
            : "Rimlink.Live.StateRunning".Translate();
        Widgets.Label(new Rect(x, y, 220f, 24f), "Rimlink.Live.HostHud".Translate(kind, LiveHost.GuestName));
        Text.Font = GameFont.Tiny;
        Widgets.Label(new Rect(x, y + 22f, 220f, 20f), $"<color=#9aa4b5>{state}</color>");
        Text.Font = GameFont.Small;
        if (Widgets.ButtonText(new Rect(x, y + 44f, 220f, 26f), "Rimlink.Live.Abort".Translate()))
            Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation("Rimlink.Live.AbortConfirm".Translate(LiveHost.GuestName), LiveHost.Abort, destructive: true));
        return 76f;
    }
}
