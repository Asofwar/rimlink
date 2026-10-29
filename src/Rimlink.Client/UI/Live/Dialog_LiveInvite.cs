using Rimlink.Client.Game;
using Rimlink.Protocol.V1;
using UnityEngine;
using Verse;

namespace Rimlink.Client.UI.Live;

/// <summary>"X wants to raid/visit you live": accept or decline within the invite window; silence counts as a decline.</summary>
public sealed class Dialog_LiveInvite : Window
{
    private readonly LiveInvite invite;
    private readonly DateTime until;
    private bool answered;

    public Dialog_LiveInvite(LiveInvite invite)
    {
        this.invite = invite;
        until = DateTime.UtcNow + TimeSpan.FromSeconds(Math.Max(5, invite.InviteSeconds));
        forcePause = true;
        absorbInputAroundWindow = true;
        closeOnClickedOutside = false;
        closeOnCancel = false;
        doCloseX = false;
    }

    public override Vector2 InitialSize => new(480f, 230f);

    public override void DoWindowContents(Rect inRect)
    {
        var s = invite.Session;
        string kind = s.Kind == LiveKind.LiveRaid ? "Rimlink.Live.KindRaid".Translate() : "Rimlink.Live.KindVisit".Translate();
        Text.Font = GameFont.Medium;
        Widgets.Label(new Rect(0f, 0f, inRect.width, 32f), "Rimlink.Live.InviteTitle".Translate(kind));
        Text.Font = GameFont.Small;
        int left = Math.Max(0, (int)(until - DateTime.UtcNow).TotalSeconds);
        Widgets.Label(new Rect(0f, 40f, inRect.width, 90f), "Rimlink.Live.Invite".Translate(s.Guest?.Name ?? "?", kind, left));
        if (Widgets.ButtonText(new Rect(0f, inRect.height - 40f, inRect.width / 2f - 8f, 32f), "Rimlink.Live.Accept".Translate())) Answer(true);
        if (Widgets.ButtonText(new Rect(inRect.width / 2f + 8f, inRect.height - 40f, inRect.width / 2f - 8f, 32f), "Rimlink.Live.Decline".Translate())) Answer(false);
        if (left == 0 && !answered)
        {
            // The server fell back already, and tells only the guest: the invite is over here too.
            if (RimlinkSession.State.Live.PendingInvite?.Session.Id == s.Id) RimlinkSession.State.Live.ClearInvite();
            Close();
        }
        if (RimlinkSession.State.Live.PendingInvite?.Session.Id != s.Id && !answered) Close(); // started, ended or fell back meanwhile
    }

    /// <summary>The Accept/Decline buttons; also called by dev tooling.</summary>
    public void Answer(bool accept)
    {
        answered = true;
        RimlinkSession.State.Live.ClearInvite();
        if (RimlinkSession.Live is { } live)
        {
            _ = Task.Run(async () =>
            {
                try { await live.Respond(invite.Session.Id, accept, CancellationToken.None); }
                catch (Exception ex) { RimlinkLog.Instance.Warn("live: the answer was not accepted: " + ex.Message); }
            });
        }
        Close();
    }
}
