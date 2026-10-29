using Rimlink.Client.Game.Live;
using Rimlink.Protocol.V1;
using UnityEngine;
using Verse;

namespace Rimlink.Client.UI.Live;

/// <summary>Waiting for the host's answer to a live proposal; Cancel takes the pawns back.</summary>
public sealed class Dialog_LiveWait : Window
{
    private readonly LiveSession session;
    private readonly DateTime since = DateTime.UtcNow;

    public Dialog_LiveWait(LiveSession session)
    {
        this.session = session;
        forcePause = true;
        absorbInputAroundWindow = true;
        closeOnClickedOutside = false;
        closeOnCancel = false;
        doCloseX = false;
    }

    public override Vector2 InitialSize => new(440f, 170f);

    public override void DoWindowContents(Rect inRect)
    {
        Text.Font = GameFont.Small;
        string kind = session.Kind == LiveKind.LiveRaid ? "Rimlink.Live.KindRaid".Translate() : "Rimlink.Live.KindVisit".Translate();
        Widgets.Label(new Rect(0f, 0f, inRect.width, 70f), "Rimlink.Live.Waiting".Translate(session.Host?.Name ?? "?", kind, (int)(DateTime.UtcNow - since).TotalSeconds));
        if (Widgets.ButtonText(new Rect(inRect.width / 2f - 80f, inRect.height - 40f, 160f, 32f), "Rimlink.Live.CancelProposal".Translate())) LiveGuest.CancelProposal();
    }
}
