using RimWorld;
using Rimlink.Client.Game;
using Rimlink.Client.Net;
using UnityEngine;
using Verse;

namespace Rimlink.Client.UI;

/// <summary>Review before local bookmark/trust or clipboard export. It never connects or follows a link.</summary>
public sealed class Dialog_ServerInvite : Window
{
    private readonly bool exporting;
    private string input = "";
    private ServerInvite? reviewed;
    private bool conflict;
    private Vector2 scroll;

    public Dialog_ServerInvite(ServerEndpoint? endpoint = null)
    {
        doCloseX = true;
        forcePause = true;
        absorbInputAroundWindow = true;
        exporting = endpoint is not null;
        if (endpoint is not null)
        {
            try
            {
                var hash = Pins().KnownFingerprint(endpoint);
                if (hash is not null) ServerInvite.TryParse($"rimlink://{endpoint.Host}:{endpoint.Port}?fp={hash}", out reviewed);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static CertificatePinStore Pins() => new(Path.Combine(RimlinkSession.DataDir, "pins.json"));
    public override Vector2 InitialSize => new(700f, 470f);

    public override void DoWindowContents(Rect rect)
    {
        Text.Font = GameFont.Medium;
        Widgets.Label(new Rect(0f, 0f, rect.width, 36f), (exporting ? "Rimlink.Invite.Export" : "Rimlink.Invite.Import").Translate());
        Text.Font = GameFont.Small;
        if (reviewed is null)
        {
            if (exporting)
            {
                Widgets.Label(new Rect(0f, 50f, rect.width, 100f), "Rimlink.Invite.NoPin".Translate());
                return;
            }
            Widgets.Label(new Rect(0f, 46f, rect.width, 60f), "Rimlink.Invite.Paste".Translate());
            input = Widgets.TextField(new Rect(0f, 110f, rect.width, 32f), input, ServerInvite.MaxLength + 1);
            if (Widgets.ButtonText(new Rect(0f, 156f, 240f, 32f), "Rimlink.Invite.Review".Translate())) Review();
            return;
        }

        string summary = "Rimlink.Invite.Summary".Translate(reviewed.Endpoint.Display, reviewed.Fingerprint) + "\n\n" +
            (exporting ? "Rimlink.Invite.ExportWarning" : conflict ? "Rimlink.Invite.Conflict" : "Rimlink.Invite.TrustWarning").Translate();
        var list = new Rect(0f, 48f, rect.width, rect.height - 146f);
        float height = Text.CalcHeight(summary, rect.width - 24f) + 8f;
        Widgets.BeginScrollView(list, ref scroll, new Rect(0f, 0f, rect.width - 20f, height));
        Widgets.Label(new Rect(0f, 0f, rect.width - 24f, height), summary);
        Widgets.EndScrollView();
        if (!conflict && Widgets.ButtonText(new Rect(0f, rect.height - 82f, rect.width, 32f),
                (exporting ? "Rimlink.Invite.ConfirmCopy" : "Rimlink.Invite.ConfirmTrust").Translate()))
        {
            if (exporting)
            {
                GUIUtility.systemCopyBuffer = reviewed.Text;
                Messages.Message("Rimlink.Invite.Copied".Translate(), MessageTypeDefOf.TaskCompletion, historical: false);
                Close();
            }
            else Import();
        }
        if (!exporting && Widgets.ButtonText(new Rect(0f, rect.height - 40f, 180f, 32f), "Rimlink.Invite.Back".Translate()))
        {
            reviewed = null;
            conflict = false;
        }
        if (Widgets.ButtonText(new Rect(rect.width - 150f, rect.height - 40f, 150f, 32f), "Cancel".Translate())) Close();
    }

    private void Review()
    {
        if (!ServerInvite.TryParse(input, out var invite))
        {
            Messages.Message("Rimlink.Invite.Invalid".Translate(), MessageTypeDefOf.RejectInput, historical: false);
            return;
        }
        try
        {
            string? existing = Pins().KnownFingerprint(invite!.Endpoint);
            conflict = existing is not null && existing != invite.Fingerprint;
            reviewed = invite;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { Failed(); }
    }

    private void Import()
    {
        try
        {
            if (!Pins().TryTrustInvite(reviewed!))
            {
                conflict = true;
                return;
            }
            var endpoint = reviewed!.Endpoint;
            if (!RimlinkMod.Settings.Servers.Select(ServerEndpoint.FromSettingsLine).Any(e => e?.Id == endpoint.Id))
            {
                RimlinkMod.Settings.Servers.Add(endpoint.ToSettingsLine());
                RimlinkMod.Settings.Write();
            }
            Messages.Message("Rimlink.Invite.Imported".Translate(), MessageTypeDefOf.TaskCompletion, historical: false);
            Close();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { Failed(); }
    }

    private static void Failed() => Messages.Message("Rimlink.Invite.SaveFailed".Translate(), MessageTypeDefOf.RejectInput, historical: false);
}
