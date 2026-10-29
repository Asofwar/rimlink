using RimBridgeServer.Sdk;
using RimWorld;
using Rimlink.Client.Game;
using Verse;

namespace Rimlink.BridgeTools;

public sealed class ColonyTools
{
    [Tool("rimlink/start_colony",
        Description = "Start a new colony in the shared world in one call: walks the new-colony pages with defaults (Crashlanded, Cassandra/Rough, " +
                      "classic ideology, the server's planet, a free random site, default pawns) and waits until the server registered the settlement. " +
                      "Needs a Rimlink session at the main menu or on the scenario page (after rimlink/connect returned NewColony). " +
                      "Waits up to timeoutSec: pass _rimBridgeTimeoutMs >= (timeoutSec+10)*1000 and the GABS call timeout accordingly.",
        ResultDescription = "success, tile, colony name and the page steps taken")]
    public async Task<object> StartColony(
        IRimBridgeContext ctx,
        [ToolParameter(Description = "Scenario name or defName; default Crashlanded")] string? scenario = null,
        [ToolParameter(Description = "Storyteller defName or label; default Cassandra")] string? storyteller = null,
        [ToolParameter(Description = "Difficulty defName or label; default Rough")] string? difficulty = null,
        [ToolParameter(Description = "World seed, only for an admin creating the server's first planet")] string? seed = null,
        [ToolParameter(Description = "Starting tile id; default a random free one")] int tile = -1,
        [ToolParameter(Description = "Seconds to wait for the whole flow (world generation included)")] int timeoutSec = 300)
    {
        if (timeoutSec < 1) return Bridge.Fail("timeoutSec must be at least 1");
        if (!Bridge.TryEnterFlow("start_colony")) return Bridge.Fail($"{Bridge.RunningFlow} is still running");
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(timeoutSec);
            var refused = await Bridge.OnMain<object?>(ctx, () =>
            {
                if (!RimlinkSession.Active) return Bridge.Fail("no Rimlink session: call rimlink/connect first");
                // A login still loading the world or a save (its wait dialog is open) would load over the new colony.
                if (Find.WindowStack.IsOpen<Rimlink.Client.UI.Dialog_Wait>()) return Bridge.Fail("a Rimlink login is still in progress");
                if (Current.ProgramState != ProgramState.Entry) return Bridge.Fail($"a new colony starts from the main menu; the game is in {Current.ProgramState}");
                if (!RimlinkSession.IsAdmin && RimlinkSession.World is not { Exists: true })
                    return Bridge.Fail("the server has no world yet: an admin starts the first colony");
                if (Pages.Top() is null) Find.WindowStack.Add(new Page_SelectScenario());
                return null;
            });
            if (refused is not null) return refused;

            var driver = new NewColonyDriver(new NewColonyDriver.Options(scenario, storyteller, difficulty, seed, tile));
            var (ok, error) = await driver.RunAsync(ctx, deadline);
            if (!ok) return Bridge.Fail(error!, new { steps = driver.Steps, topWindow = await Bridge.OnMain(ctx, Bridge.TopWindowType) });

            // The settlement goes to the server in the background; a refusal shows a message box instead.
            int left = Math.Max(1, (int)(deadline - DateTime.UtcNow).TotalSeconds);
            bool settled = await Bridge.WaitUntil(ctx, () => Settled() || Find.WindowStack.IsOpen<Dialog_MessageBox>(), left);
            return await Bridge.OnMain<object>(ctx, () =>
            {
                if (!settled || !Settled())
                {
                    string? message = Find.WindowStack.WindowOfType<Dialog_MessageBox>()?.text.ToString();
                    return Bridge.Fail(message ?? "the server did not confirm the settlement in time", new { steps = driver.Steps });
                }
                return new { success = true, tile = (int)Find.CurrentMap.Tile, colony = Faction.OfPlayer.Name, steps = driver.Steps };
            });
        }
        finally
        {
            Bridge.ExitFlow();
        }
    }

    /// <summary>Main thread: the server knows our settlement on the current map's tile.</summary>
    private static bool Settled()
    {
        if (Find.CurrentMap is not { } map) return false;
        int tile = map.Tile;
        long me = RimlinkSession.MyPlayerId;
        return RimlinkSession.State.Objects.All.Any(o => o.Owner?.PlayerId == me && o.Tile == tile);
    }
}
