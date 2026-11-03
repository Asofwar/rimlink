using Rimlink.Client.Mods.Delivery;
using Rimlink.Client.Net;
using Rimlink.Protocol.ModpackDelivery;
using UnityEngine;
using Verse;

namespace Rimlink.Client.UI;

public sealed class Dialog_ModpackDownload : Window
{
    private readonly TaskCompletionSource<bool> answer;
    private readonly string summary;
    private readonly bool hasSettings;
    private bool acceptPackage, acceptSettings;
    private Vector2 scroll;
    public Dialog_ModpackDownload(ServerEndpoint endpoint, DeliveryCapabilities capabilities, PackageManifest manifest, SettingsSnapshot settings, ModpackInstallPlan plan, TaskCompletionSource<bool> answer)
    {
        this.answer = answer; hasSettings = settings.Configs.Length > 0; forcePause = true; absorbInputAroundWindow = true; doCloseX = true;
        summary = "Rimlink.Delivery.Summary".Translate(endpoint.Display, manifest.Mods.Length, (manifest.ArchiveBytes / 1048576.0).ToString("0.0"), (manifest.ExpandedBytes / 1048576.0).ToString("0.0")) + "\n\n"
            + string.Join("\n", plan.Mods.Select(m => (m.ReuseRoot is null ? "Rimlink.Delivery.Install" : "Rimlink.Delivery.Reuse").Translate(m.Mod.DisplayName, m.Mod.PackageId, m.Mod.Version)))
            + (plan.Retire.Length > 0 ? "\n\n" + "Rimlink.Delivery.Retire".Translate(plan.Retire.Length).ToString() : "")
            + "\n\n" + "Rimlink.Delivery.Policy".Translate(capabilities.PasswordLoginAvailable ? "Rimlink.Delivery.PasswordAuth".Translate() : "Rimlink.Delivery.SteamAuth".Translate());
    }
    public override Vector2 InitialSize => new(720f, 600f);
    public override void DoWindowContents(Rect rect)
    {
        Text.Font = GameFont.Medium; Widgets.Label(new(0, 0, rect.width, 36), "Rimlink.Delivery.Title".Translate()); Text.Font = GameFont.Small;
        var viewport = new Rect(0, 42, rect.width, rect.height - 220);
        float height = Text.CalcHeight(summary, viewport.width - 20);
        Widgets.BeginScrollView(viewport, ref scroll, new(0, 0, viewport.width - 20, Math.Max(viewport.height, height)));
        Widgets.Label(new(0, 0, viewport.width - 20, height), summary); Widgets.EndScrollView();
        float y = rect.height - 170;
        Widgets.CheckboxLabeled(new(0, y, rect.width, 60), "Rimlink.Delivery.Consent".Translate(), ref acceptPackage);
        if (hasSettings) Widgets.CheckboxLabeled(new(0, y + 64, rect.width, 54), "Rimlink.Delivery.SettingsConsent".Translate(), ref acceptSettings);
        if (Widgets.ButtonText(new(0, rect.height - 36, rect.width / 2 - 8, 32), "Rimlink.Delivery.InstallRestart".Translate(), active: acceptPackage && (!hasSettings || acceptSettings))) { answer.TrySetResult(true); Close(); }
        if (Widgets.ButtonText(new(rect.width / 2 + 8, rect.height - 36, rect.width / 2 - 8, 32), "Rimlink.Cancel".Translate())) Close();
    }
    public override void PreClose() { answer.TrySetResult(false); base.PreClose(); }
}
