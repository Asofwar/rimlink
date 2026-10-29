using HarmonyLib;
using RimBridgeServer.Sdk;
using RimWorld;
using RimWorld.Planet;
using Rimlink.Client.Game;
using Verse;

namespace Rimlink.BridgeTools;

/// <summary>
/// Walks the vanilla new-colony pages the way a player pressing Next does, with defaults filled in: scenario, storyteller,
/// ideology, world (the Rimlink patch locks it to the server's planet), starting site, pawns. One action per frame.
/// </summary>
public sealed class NewColonyDriver(NewColonyDriver.Options options)
{
    public sealed record Options(string? Scenario, string? Storyteller, string? Difficulty, string? Seed, int Tile);

    private enum Kind { Wait, Acted, Done, Failed }

    private sealed record Step(Kind Kind, string? Text = null);

    private const int MaxActions = 40;
    private const int SiteTries = 20;

    private int worldGenerations;

    public List<string> Steps { get; } = [];

    public async Task<(bool ok, string? error)> RunAsync(IRimBridgeContext ctx, DateTime deadline)
    {
        int actions = 0;
        while (DateTime.UtcNow < deadline)
        {
            var step = await Bridge.OnMain(ctx, Advance);
            switch (step.Kind)
            {
                case Kind.Done: return (true, null);
                case Kind.Failed: return (false, step.Text);
                case Kind.Acted:
                    Steps.Add(step.Text!);
                    if (++actions > MaxActions) return (false, $"more than {MaxActions} page actions");
                    break;
            }
            await ctx.Game.NextFrameAsync();
        }
        return (false, "timed out");
    }

    /// <summary>Main thread: one look at the window stack, at most one action.</summary>
    private Step Advance()
    {
        if (Current.ProgramState == ProgramState.Playing && Find.CurrentMap != null && !LongEventHandler.AnyEventNowOrWaiting) return new(Kind.Done);
        if (LongEventHandler.AnyEventNowOrWaiting) return new(Kind.Wait);

        if (Bridge.TopWindow() is Dialog_MessageBox box)
        {
            // Two buttons: a page's "are you sure" (settle near a faction, disabled work types). One button: an error.
            string text = box.text.ToString();
            if (box.buttonBText is null) return new(Kind.Failed, "message: " + text);
            box.buttonAAction?.Invoke();
            box.Close();
            return new(Kind.Acted, "confirmed: " + Short(text));
        }

        var page = Pages.Top();
        if (page is null) return new(Kind.Wait);
        string name = page.GetType().Name;

        switch (page)
        {
            case Page_SelectScenario:
                var scenario = FindScenario(options.Scenario);
                if (scenario is null)
                    return new(Kind.Failed, $"unknown scenario '{options.Scenario}'; known: {string.Join(", ", ScenarioLister.AllScenarios().Select(s => s.name))}");
                AccessTools.Field(typeof(Page_SelectScenario), "curScen").SetValue(page, scenario);
                return Next(page, $"scenario {scenario.name}");

            case Page_SelectStoryteller:
                var teller = FindDef<StorytellerDef>(options.Storyteller) ?? (options.Storyteller is null ? StorytellerDefOf.Cassandra : null);
                var difficulty = FindDef<DifficultyDef>(options.Difficulty) ?? (options.Difficulty is null ? DifficultyDefOf.Rough : null);
                if (teller is null) return new(Kind.Failed, $"unknown storyteller '{options.Storyteller}'");
                if (difficulty is null) return new(Kind.Failed, $"unknown difficulty '{options.Difficulty}'");
                var t = Traverse.Create(page);
                t.Field("storyteller").SetValue(teller);
                t.Field("difficulty").SetValue(difficulty);
                t.Field("difficultyValues").SetValue(new Difficulty(difficulty));
                Find.GameInitData.permadeathChosen = true;
                Find.GameInitData.permadeath = false;
                return Next(page, $"storyteller {teller.defName}, {difficulty.defName}");

            case Page_ChooseIdeoPreset:
                var selection = AccessTools.Field(typeof(Page_ChooseIdeoPreset), "presetSelection");
                selection.SetValue(page, Enum.ToObject(selection.FieldType, 0)); // PresetSelection.Classic
                return Next(page, "ideology classic");

            case Page_CreateWorldParams:
                if (++worldGenerations > 1) return new(Kind.Failed, "world generation did not finish (the world page is still open)");
                bool serverWorld = RimlinkSession.World is { Exists: true };
                if (!serverWorld && options.Seed is { Length: > 0 } seed) Traverse.Create(page).Field("seedString").SetValue(seed);
                return Next(page, serverWorld ? "world generation (server's planet)" : "world generation (new planet)");

            case Page_SelectStartingSite:
                // The admin's planet is registered in the background after generation; settling needs it on the server.
                if (RimlinkSession.World is not { Exists: true }) return new(Kind.Wait);
                var tile = PickTile();
                if (tile is null) return new(Kind.Failed, options.Tile >= 0 ? $"tile {options.Tile} cannot be settled" : $"no free starting tile in {SiteTries} tries");
                Find.WorldInterface.SelectedTile = tile.Value;
                return Next(page, $"site {(int)tile.Value}");

            default:
                return Next(page, "next on " + name);
        }
    }

    private static Step Next(Page page, string text) =>
        Pages.TryNext(page, out string? refusal) ? new(Kind.Acted, text) : new(Kind.Failed, $"{page.GetType().Name} refused next: {refusal}");

    private PlanetTile? PickTile()
    {
        if (options.Tile >= 0)
        {
            PlanetTile wanted = options.Tile; // surface layer, as ForeignSettlements places them
            return TileFinder.IsValidTileForNewSettlement(wanted) ? wanted : null;
        }
        // A random tile can be another player's settlement (Rimlink places them before this page): try again.
        for (int i = 0; i < SiteTries; i++)
        {
            var candidate = TileFinder.RandomStartingTile();
            if (candidate.Valid && TileFinder.IsValidTileForNewSettlement(candidate)) return candidate;
        }
        return null;
    }

    private static Scenario? FindScenario(string? wanted)
    {
        if (string.IsNullOrWhiteSpace(wanted)) return ScenarioDefOf.Crashlanded.scenario;
        return ScenarioLister.AllScenarios().FirstOrDefault(s => string.Equals(s.name, wanted, StringComparison.OrdinalIgnoreCase))
            ?? DefDatabase<ScenarioDef>.AllDefs.FirstOrDefault(d => string.Equals(d.defName, wanted, StringComparison.OrdinalIgnoreCase))?.scenario;
    }

    private static T? FindDef<T>(string? wanted) where T : Def =>
        string.IsNullOrWhiteSpace(wanted)
            ? null
            : DefDatabase<T>.AllDefs.FirstOrDefault(d => string.Equals(d.defName, wanted, StringComparison.OrdinalIgnoreCase)
                                                        || string.Equals(d.label, wanted, StringComparison.OrdinalIgnoreCase));

    private static string Short(string text) => text.Length <= 80 ? text : text[..80] + "…";
}
