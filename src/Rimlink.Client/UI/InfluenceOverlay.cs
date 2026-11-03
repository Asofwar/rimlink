using System.Reflection;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Rimlink.Client.Game;
using Rimlink.Client.Game.Influence;
using Rimlink.Client.Net;
using Rimlink.Protocol.V1;
using UnityEngine;
using Verse;

namespace Rimlink.Client.UI;

public static class InfluenceOverlay
{
    private static bool legend;
    private static Vector2 scroll;
    public static string T(string key) => ("Rimlink.Influence." + key).Translate();

    public static void Draw()
    {
        if (!RimlinkSession.Active || !WorldRendererUtility.WorldSelected) return;
        float width = 260, x = Verse.UI.screenWidth - width - 18, y = 148;
        bool enabled = InfluenceLayerController.Enabled, before = enabled;
        Widgets.DrawWindowBackground(new Rect(x - 8, y - 8, width + 16, 108));
        Widgets.CheckboxLabeled(new Rect(x, y, width, 28), T("Toggle"), ref enabled);
        if (enabled != before) InfluenceLayerController.SetEnabled(enabled);
        if (Widgets.ButtonText(new Rect(x, y + 34, width / 2 - 4, 28), T(legend ? "HideLegend" : "Legend"))) legend = !legend;
        if (Widgets.ButtonText(new Rect(x + width / 2 + 4, y + 34, width / 2 - 4, 28), T("Settings"))) OpenSettings();
        var cache = RimlinkSession.State.Influence;
        string? state = cache.Unavailable ? T("Unavailable") : cache.Current is null ? T("Loading") : cache.Stale ? T("Stale") : cache.Current.Sources.Count == 0 ? T("Empty") : null;
        if (state is not null) Widgets.Label(new Rect(x, y + 67, width, 30), state);
        if (!legend || !enabled || cache.Current is not { } snapshot) return;
        float height = Math.Min(Math.Min(330, snapshot.Alliances.Count * 46 + 42), Verse.UI.screenHeight - y - 220);
        y += 112;
        Widgets.DrawWindowBackground(new Rect(x - 8, y - 8, width + 16, height + 16));
        var area = new Rect(x, y, width, height);
        Widgets.BeginScrollView(area, ref scroll, new Rect(0, 0, width - 18, snapshot.Alliances.Count * 46 + 42));
        float row = 0;
        long own = RimlinkSession.State.Community.Current.Alliance?.Id ?? 0;
        foreach (var group in snapshot.Alliances)
        {
            Widgets.DrawBoxSolid(new Rect(0, row + 6, 18, 18), InfluenceColors.Palette[group.PaletteIndex]);
            string label = group.Name + (group.Id == own ? " (" + T("Own") + ")" : "");
            if (Widgets.ButtonText(new Rect(27, row, width - 47, 40), label)) InfluenceLayerController.FocusAlliance(group.Id);
            row += 46;
        }
        Widgets.Label(new Rect(0, row + 4, width - 18, 38), T("DisputedLegend"));
        Widgets.EndScrollView();
    }

    public static void OpenSettings()
    {
        if (!Find.WindowStack.Windows.Any(w => w is Dialog_Influence)) Find.WindowStack.Add(new Dialog_Influence());
    }

    public static string Inspect()
    {
        if (!InfluenceLayerController.Visible || RimlinkSession.State.Influence.Current is not { } snapshot
            || Find.World is null || Find.WorldSelector is not { } selector || Find.WorldGrid?.Surface is not { } surface) return "";
        var selected = selector.SelectedTile;
        if (selected.tileId < 0 || selected.Layer != surface
            || InfluenceLayerController.Map is not { } map || !map.Cells.TryGetValue(selected.tileId, out var cell)) return "";
        var groups = snapshot.Alliances.ToDictionary(g => g.Id, g => g.Name);
        var lines = new List<string> { cell.Disputed ? T("Disputed") : T("Zone") + ": " + groups[cell.Contenders[0].AllianceId] };
        foreach (var claim in cell.Contenders)
        {
            var source = snapshot.Sources.First(s => s.Id == claim.SourceId);
            string name = RimlinkSession.State.Objects.TryGet(source.Id)?.Name ?? "#" + source.Id;
            lines.Add("Rimlink.Influence.Source".Translate(groups[claim.AllianceId], name, source.Tile, claim.Distance));
        }
        if (RimlinkSession.State.Influence.Stale) lines.Add(T("Stale"));
        return string.Join("\n", lines);
    }
}

public sealed class Dialog_Influence : Window
{
    private InfluenceSetting? setting;
    private long settlement;
    private bool loading, loaded;
    private string status = "";
    public Dialog_Influence() { doCloseX = true; draggable = true; forcePause = false; absorbInputAroundWindow = false; }
    public override Vector2 InitialSize => new(660, 490);

    public override void DoWindowContents(Rect rect)
    {
        CommunitySync.Tick();
        var own = RimlinkSession.State.Objects.All.Where(o => o.Type == "settlement" && o.Owner.PlayerId == RimlinkSession.MyPlayerId).OrderBy(o => o.Id).ToArray();
        if (settlement == 0 && own.Length > 0) settlement = own[0].Id;
        if (!loaded && settlement != 0) { loaded = true; Refresh(); }
        Text.Font = GameFont.Medium; Widgets.Label(new Rect(0, 0, rect.width, 34), InfluenceOverlay.T("Title")); Text.Font = GameFont.Small;
        Widgets.Label(new Rect(0, 42, rect.width, 88), InfluenceOverlay.T("Explanation"));
        if (own.Length == 0) Widgets.Label(new Rect(0, 136, rect.width, 32), InfluenceOverlay.T("NoColony"));
        else
        {
            var home = own.FirstOrDefault(o => o.Id == settlement) ?? own[0];
            if (Widgets.ButtonText(new Rect(0, 136, rect.width - 130, 30), home.Name + " (#" + home.Tile + ")") && !loading)
                Find.WindowStack.Add(new FloatMenu(own.Select(o => new FloatMenuOption(o.Name + " (#" + o.Tile + ")", () => { settlement = o.Id; setting = null; Refresh(); })).ToList()));
            if (Widgets.ButtonText(new Rect(rect.width - 120, 136, 120, 30), InfluenceOverlay.T("Refresh")) && !loading) Refresh();
            Widgets.Label(new Rect(0, 177, rect.width, 30), loading ? InfluenceOverlay.T("Loading") : setting is null ? InfluenceOverlay.T("Unavailable") : InfluenceOverlay.T(setting.Enabled ? "Published" : "Hidden"));
            bool was = GUI.enabled; GUI.enabled = was && !loading && setting is not null;
            if (Widgets.ButtonText(new Rect(0, 216, rect.width / 2 - 6, 32), InfluenceOverlay.T("Publish"))) Publish();
            if (Widgets.ButtonText(new Rect(rect.width / 2 + 6, 216, rect.width / 2 - 6, 32), InfluenceOverlay.T("Disable"))) Disable();
            GUI.enabled = was;
        }
        var alliance = RimlinkSession.State.Community.Current.Alliance;
        if (alliance?.OwnerId == RimlinkSession.MyPlayerId)
        {
            Widgets.Label(new Rect(0, 271, rect.width, 28), InfluenceOverlay.T("Color"));
            for (int i = 0; i < InfluenceColors.Palette.Length; i++)
            {
                int index = i; var swatch = new Rect(i * 36, 308, 30, 30);
                Widgets.DrawBoxSolid(swatch, InfluenceColors.Palette[i]);
                TooltipHandler.TipRegion(swatch, "Rimlink.Influence.ColorNumber".Translate(i + 1));
                if (Widgets.ButtonInvisible(swatch) && !loading) Color(alliance.Id, index);
            }
        }
        Widgets.Label(new Rect(0, 362, rect.width, rect.height - 362), status);
    }

    private void Refresh()
    {
        if (RimlinkSession.Influence is not { } api || RimlinkSession.Connection is not { } connection) return;
        loading = true; long id = settlement;
        _ = Task.Run(async () =>
        {
            try
            {
                var next = await api.GetSetting(new InfluenceTarget { SettlementId = id }, CancellationToken.None);
                MainThread.Post(() => { if (RimlinkSession.Connection == connection && settlement == id) setting = next; });
            }
            catch (Exception ex) { MainThread.Post(() => status = RefusalText.Of(RpcErrorDetail.Read(ex))); }
            finally { MainThread.Post(() => loading = false); }
        });
    }

    private void Publish()
    {
        if (setting is not { } state) return;
        if (!InfluenceSurfaceCapture.TryOwnColony(state.Tile, out var proof)) { status = InfluenceOverlay.T("NeedHome"); return; }
        if (!proof.Root || proof.WorldId != state.Context.WorldInstanceId || proof.Generation != state.Context.SaveGeneration) { status = InfluenceOverlay.T("ContextChanged"); return; }
        if (state.Context.AllianceId == 0) { status = InfluenceOverlay.T("NoAlliance"); return; }
        Run(async (api, outbox) =>
        {
            var confirmed = await api.Submit(new InfluenceMutation { Surface = new InfluenceSurface { Context = state.Context, ExpectedVersion = state.Version, Tile = proof.Tile, RootSurface = proof.Root } }, outbox, CancellationToken.None);
            await api.Submit(new InfluenceMutation { Publication = new InfluencePublication { Context = state.Context, ExpectedVersion = confirmed.AppliedVersion, Enabled = true } }, outbox, CancellationToken.None);
        });
    }

    private void Disable()
    {
        if (setting is not { } state) return;
        Run(async (api, outbox) => { await api.Submit(new InfluenceMutation { Publication = new InfluencePublication { Context = state.Context, ExpectedVersion = state.Version, Enabled = false } }, outbox, CancellationToken.None); });
    }

    private void Color(long alliance, int index)
    {
        string world = RimlinkSession.World?.WorldInstanceId ?? "";
        Run(async (api, outbox) => { await api.Submit(new InfluenceMutation { Color = new InfluenceColor { AllianceId = alliance, WorldInstanceId = world, PaletteIndex = index } }, outbox, CancellationToken.None); });
    }

    private void Run(Func<Rimlink.Client.Influence.InfluenceClient, Rimlink.Client.Economy.TransferOutbox, Task> operation)
    {
        if (loading || RimlinkSession.Influence is not { } api || RimlinkSession.Outbox is not { } outbox || RimlinkSession.Connection is not { } connection) return;
        loading = true; status = InfluenceOverlay.T("Pending");
        _ = Task.Run(async () =>
        {
            Exception? failure = null;
            try
            {
                await operation(api, outbox);
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                MainThread.Post(() =>
                {
                    if (RimlinkSession.Connection != connection) return;
                    loading = false;
                    if (failure is not null) status = RefusalText.Of(RpcErrorDetail.Read(failure));
                    else { status = InfluenceOverlay.T("Updated"); RimlinkSession.State.Influence.Invalidate(); Refresh(); }
                });
            }
        });
    }
}

[HarmonyPatch(typeof(WorldInterface), nameof(WorldInterface.WorldInterfaceOnGUI))]
public static class Patch_WorldInterface_WorldInterfaceOnGUI_Influence
{
    public static void Postfix() => InfluenceOverlay.Draw();
}

[HarmonyPatch]
public static class Patch_WorldInspectPane_Influence
{
    public static IEnumerable<MethodBase> TargetMethods()
    {
        if (AccessTools.PropertyGetter(typeof(WorldInspectPane), "TileInspectString") is { } getter) yield return getter;
    }
    public static void Postfix(ref string __result)
    {
        string extra = InfluenceOverlay.Inspect();
        if (extra.Length != 0) __result += "\n" + extra;
    }
}
