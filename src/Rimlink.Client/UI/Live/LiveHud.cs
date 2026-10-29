using Rimlink.Client.Game.Live;
using Rimlink.Protocol.V1;
using UnityEngine;
using Verse;

namespace Rimlink.Client.UI.Live;

/// <summary>The host's corner of the overlay during a live session: who is here, the tempo, and the abort button.</summary>
public static class LiveHud
{
    /// <summary>Draws at (x, y) and returns the height used.</summary>
    public static float DrawHost(float x, float y)
    {
        if (!LiveHost.Active && !LiveHost.Starting) return 0f;
        Text.Font = GameFont.Small;
        string kind = LiveHost.Session?.Kind == LiveKind.LiveRaid ? "Rimlink.Live.KindRaid".Translate() : "Rimlink.Live.KindVisit".Translate();
        string state = LiveHost.Starting ? "Rimlink.Live.StateStarting".Translate()
            : !LiveHost.GuestHere ? "Rimlink.Live.StateWaiting".Translate()
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
