using Rimlink.Protocol.V1;
using UnityEngine;
using Verse;

namespace Rimlink.Client.UI;

/// <summary>Shows why the server refused the mod set and offers the fixes.</summary>
public sealed class Dialog_ModpackDiff : Window
{
    private readonly ModpackDiff diff;
    private Vector2 scroll;
    private string status = "";

    public Dialog_ModpackDiff(ModpackDiff diff)
    {
        this.diff = diff;
        doCloseX = true;
        forcePause = true;
        absorbInputAroundWindow = true;
    }

    public override Vector2 InitialSize => new(700f, 560f);

    public override void DoWindowContents(Rect inRect)
    {
        Text.Font = GameFont.Medium;
        Widgets.Label(new Rect(0f, 0f, inRect.width, 36f), "Rimlink.Modpack.Title".Translate());
        Text.Font = GameFont.Small;

        var lines = new List<string>();
        if (diff.Missing.Count > 0) lines.Add("Rimlink.Modpack.Missing".Translate(diff.Missing.Count));
        lines.AddRange(diff.Missing.Select(m => $"  - {m.PackageId} {m.Version}{(m.WorkshopId != 0 ? " (Steam)" : m.Url.Length > 0 ? " (" + m.Url + ")" : "")}"));
        if (diff.WrongVersion.Count > 0) lines.Add("Rimlink.Modpack.WrongVersion".Translate(diff.WrongVersion.Count));
        lines.AddRange(diff.WrongVersion.Select(m => $"  - {m.PackageId}: {m.ClientVersion} → {m.ServerVersion}"));
        if (diff.Extra.Count > 0) lines.Add("Rimlink.Modpack.Extra".Translate(diff.Extra.Count));
        lines.AddRange(diff.Extra.Select(m => $"  - {m.PackageId}"));
        string text = string.Join("\n", lines);

        var listRect = new Rect(0f, 44f, inRect.width, inRect.height - 44f - 130f);
        float height = Text.CalcHeight(text, listRect.width - 16f);
        Widgets.BeginScrollView(listRect, ref scroll, new Rect(0f, 0f, listRect.width - 16f, Math.Max(height, listRect.height)));
        Widgets.Label(new Rect(0f, 0f, listRect.width - 16f, height), text);
        Widgets.EndScrollView();

        float y = inRect.height - 120f;
        Widgets.Label(new Rect(0f, y, inRect.width, 28f), status);
        y += 32f;
        float w = (inRect.width - 24f) / 4f;
        if (Widgets.ButtonText(new Rect(0f, y, w, 32f), "Rimlink.Modpack.Subscribe".Translate()))
            status = "Rimlink.Modpack.Subscribed".Translate(ModpackActions.SubscribeMissing(diff));
        if (Widgets.ButtonText(new Rect(w + 8f, y, w, 32f), "Rimlink.Modpack.OpenLinks".Translate()))
            status = "Rimlink.Modpack.Opened".Translate(ModpackActions.OpenUrls(diff));
        if (Widgets.ButtonText(new Rect(2f * (w + 8f), y, w, 32f), "Rimlink.Modpack.ApplyOrder".Translate()))
        {
            ModpackActions.ApplyServerOrder(diff);
            status = "Rimlink.Modpack.OrderApplied".Translate();
        }
        if (Widgets.ButtonText(new Rect(3f * (w + 8f), y, w, 32f), "Rimlink.Modpack.Restart".Translate()))
            ModpackActions.Restart();
    }
}
