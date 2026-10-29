using Rimlink.Client.Game;
using Rimlink.Client.Mods;
using Rimlink.Client.Net;
using Rimlink.Protocol.V1;
using UnityEngine;
using Verse;

namespace Rimlink.Client.UI;

/// <summary>Why the server refused the mod set, category by category with who fixes it, and the fixes (spec §5).</summary>
public sealed class Dialog_ModpackDiff : Window
{
    private readonly ModpackDiff diff;
    private readonly ServerEndpoint endpoint;
    private readonly string? login;
    private readonly string text;
    private Vector2 scroll;
    private string status = "";
    private bool fixing;

    public Dialog_ModpackDiff(ModpackDiff diff, ServerEndpoint endpoint, string? login)
    {
        this.diff = diff;
        this.endpoint = endpoint;
        this.login = login;
        text = Describe(diff);
        doCloseX = true;
        forcePause = true;
        absorbInputAroundWindow = true;
    }

    public override Vector2 InitialSize => new(760f, 600f);

    private static string Describe(ModpackDiff diff)
    {
        var lines = new List<string>();
        foreach (var section in ModpackDiffView.Sections(diff))
        {
            if (lines.Count > 0) lines.Add("");
            int count = section.Rows.Count;
            switch (section.Category)
            {
                case DiffCategory.Missing:
                    lines.Add("Rimlink.Modpack.Missing".Translate(count));
                    lines.Add(Hint("Rimlink.Modpack.Missing.Hint".Translate()));
                    break;
                case DiffCategory.Version:
                    lines.Add("Rimlink.Modpack.WrongVersion".Translate(count));
                    break;
                case DiffCategory.Files:
                    lines.Add("Rimlink.Modpack.Files".Translate(count));
                    lines.Add(Hint("Rimlink.Modpack.Files.Hint".Translate()));
                    break;
                case DiffCategory.Extra:
                    lines.Add("Rimlink.Modpack.Extra".Translate(count));
                    lines.Add(Hint("Rimlink.Modpack.Extra.Hint".Translate()));
                    break;
                case DiffCategory.Order:
                    lines.Add("Rimlink.Modpack.Order".Translate());
                    lines.Add(Hint("Rimlink.Modpack.Order.Hint".Translate()));
                    break;
                case DiffCategory.Settings:
                    lines.Add("Rimlink.Modpack.Settings".Translate(count));
                    lines.Add(Hint("Rimlink.Modpack.Settings.Hint".Translate()));
                    break;
            }
            foreach (var row in section.Rows)
            {
                string line = row.Detail.Length > 0 ? $"  - {row.PackageId}: {row.Detail}" : $"  - {row.PackageId}";
                if (section.Category == DiffCategory.Version)
                    line += " — " + (row.Side == FixSide.Server ? "Rimlink.Modpack.Version.Server" : "Rimlink.Modpack.Version.Player").Translate();
                lines.Add(line);
            }
        }
        return string.Join("\n", lines);
    }

    private static string Hint(string text) => $"<color=#9aa4b5>{text}</color>";

    public override void DoWindowContents(Rect inRect)
    {
        Text.Font = GameFont.Medium;
        Widgets.Label(new Rect(0f, 0f, inRect.width, 36f), "Rimlink.Modpack.Title".Translate());
        Text.Font = GameFont.Small;

        var listRect = new Rect(0f, 44f, inRect.width, inRect.height - 44f - 90f);
        float height = Text.CalcHeight(text, listRect.width - 16f);
        Widgets.BeginScrollView(listRect, ref scroll, new Rect(0f, 0f, listRect.width - 16f, Math.Max(height, listRect.height)));
        Widgets.Label(new Rect(0f, 0f, listRect.width - 16f, height), text);
        Widgets.EndScrollView();

        if (fixing) ContinueFix();

        float y = inRect.height - 80f;
        Widgets.Label(new Rect(0f, y, inRect.width, 40f), status);
        y += 44f;
        float w = (inRect.width - 24f) / 4f;
        if (Widgets.ButtonText(new Rect(0f, y, w, 32f), "Rimlink.Modpack.Subscribe".Translate()))
            status = "Rimlink.Modpack.Subscribed".Translate(ModpackActions.SubscribeMissing(diff));
        if (Widgets.ButtonText(new Rect(w + 8f, y, w, 32f), "Rimlink.Modpack.OpenLinks".Translate()))
            status = "Rimlink.Modpack.Opened".Translate(ModpackActions.OpenUrls(diff));
        if (Widgets.ButtonText(new Rect(2f * (w + 8f), y, w, 32f), "Rimlink.Modpack.FixRestart".Translate(), active: !fixing))
        {
            ModpackActions.SubscribeMissing(diff);
            fixing = true;
        }
        if (Widgets.ButtonText(new Rect(3f * (w + 8f), y, w, 32f), "CloseButton".Translate())) Close();
    }

    /// <summary>Waits for Steam to install subscribed mods, then fixes and restarts; a write failure stops it with the reason.</summary>
    private void ContinueFix()
    {
        int pending = ModpackActions.PendingDownloads(diff);
        if (pending > 0)
        {
            status = "Rimlink.Modpack.Downloading".Translate(pending);
            return;
        }
        fixing = false;
        try
        {
            ModpackActions.FixAndRestart(diff, endpoint, login);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            status = "Rimlink.Modpack.SettingsWriteFailed".Translate(ex.Message);
            RimlinkLog.Instance.Warn("fix and restart failed: " + ex.Message);
        }
    }
}
