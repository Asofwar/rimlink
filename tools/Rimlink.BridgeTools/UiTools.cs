using RimBridgeServer.Sdk;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace Rimlink.BridgeTools;

/// <summary>UI the bridge's layout snapshot cannot reach: main-menu options and page buttons (Next/Back/Select random site).</summary>
public sealed class UiTools
{
    [Tool("rimlink/menu_options", Description = "List the main-menu options as drawn (including Rimlink's Multiplayer entry)",
        ResultDescription = "success and options [{index, label}]")]
    public async Task<object> MenuOptions(IRimBridgeContext ctx)
    {
        var error = await Capture(ctx);
        if (error is not null) return error;
        return await Bridge.OnMain<object>(ctx, () => new
        {
            success = true,
            options = MenuCapture.Last.Select((o, i) => new { index = i, label = o.label }).ToList(),
        });
    }

    [Tool("rimlink/click_menu_option", Description = "Activate a main-menu option by its label (case-insensitive), as a click does",
        ResultDescription = "success, the clicked label and the top window afterwards")]
    public async Task<object> ClickMenuOption(IRimBridgeContext ctx,
        [ToolParameter(Description = "Option label as listed by rimlink/menu_options", Required = true)] string label)
    {
        var error = await Capture(ctx);
        if (error is not null) return error;
        var clicked = await Bridge.OnMain<object?>(ctx, () =>
        {
            var option = MenuCapture.Last.FirstOrDefault(o => string.Equals(o.label?.Trim(), label.Trim(), StringComparison.OrdinalIgnoreCase));
            if (option?.action is null)
                return Bridge.Fail($"no menu option '{label}'", new { options = MenuCapture.Last.Select(o => o.label).ToList() });
            option.action();
            return null;
        });
        if (clicked is not null) return clicked;
        await ctx.Game.NextFrameAsync();
        return new { success = true, clicked = label, topWindow = await Bridge.OnMain(ctx, Bridge.TopWindowType) };
    }

    [Tool("rimlink/page_state", Description = "The top Page window (new-colony pages and others): type, title, whether Back is allowed, selected tile",
        ResultDescription = "success, page, title, canBack, selectedTile")]
    public Task<object> PageState(IRimBridgeContext ctx) => Bridge.OnMain(ctx, State);

    [Tool("rimlink/page_action", Description = "Press a button of the top Page: next, back, or random_site (Select random site on the starting-site page)",
        ResultDescription = "success and the page state afterwards")]
    public async Task<object> PageAction(IRimBridgeContext ctx,
        [ToolParameter(Description = "next, back or random_site", Required = true)] string action)
    {
        var result = await Bridge.OnMain<object?>(ctx, () =>
        {
            var page = Pages.Top();
            if (page is null) return Bridge.Fail("no page open", new { topWindow = Bridge.TopWindowType() });
            switch (action)
            {
                case "next":
                    return Pages.TryNext(page, out string? refusal) ? null : Bridge.Fail($"{page.GetType().Name} refused next: {refusal}");
                case "back":
                    return Pages.TryBack(page) ? null : Bridge.Fail($"{page.GetType().Name} refused back");
                case "random_site":
                    if (page is not Page_SelectStartingSite) return Bridge.Fail($"random_site needs the starting-site page, not {page.GetType().Name}");
                    Find.WorldInterface.SelectedTile = TileFinder.RandomStartingTile();
                    Find.WorldCameraDriver.JumpTo(Find.WorldGrid.GetTileCenter(Find.WorldInterface.SelectedTile));
                    return null;
                default:
                    return Bridge.Fail("action must be next, back or random_site");
            }
        });
        if (result is not null) return result;
        await ctx.Game.NextFrameAsync();
        return await Bridge.OnMain(ctx, State);
    }

    private static object State()
    {
        var page = Pages.Top();
        if (page is null) return Bridge.Fail("no page open", new { topWindow = Bridge.TopWindowType() });
        PlanetTile? tile = page is Page_SelectStartingSite ? Find.WorldInterface.SelectedTile : null;
        return new
        {
            success = true,
            page = page.GetType().Name,
            title = page.PageTitle,
            canBack = Pages.CanBack(page),
            selectedTile = tile is { Valid: true } t ? (int?)(int)t : null,
        };
    }

    /// <summary>Patches the menu drawing if needed and lets two frames draw it; null when the options are there.</summary>
    private static async Task<object?> Capture(IRimBridgeContext ctx)
    {
        var refused = await Bridge.OnMain<object?>(ctx, () =>
        {
            if (Current.ProgramState != ProgramState.Entry) return Bridge.Fail($"the main menu is not shown; the game is in {Current.ProgramState}");
            MenuCapture.Ensure();
            return null;
        });
        if (refused is not null) return refused;
        await ctx.Game.FramesAsync(3);
        bool drawn = await Bridge.OnMain(ctx, () => MenuCapture.Last.Count > 0);
        return drawn ? null : Bridge.Fail("the main-menu options were not drawn in the last frames");
    }
}
