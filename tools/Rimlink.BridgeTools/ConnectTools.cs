using System.Diagnostics;
using RimBridgeServer.Sdk;
using Rimlink.Client;
using Rimlink.Client.Game;
using Rimlink.Client.Net;
using Rimlink.Client.UI;
using Verse;

namespace Rimlink.BridgeTools;

public sealed class ConnectTools
{
    [Tool("rimlink/connect",
        Description = "From the main menu: add the server if missing, log in with login/password (register on refusal when register=auto), " +
                      "trust a new certificate, and wait for the outcome. Waits up to timeoutSec: pass _rimBridgeTimeoutMs >= (timeoutSec+10)*1000 " +
                      "and the GABS call timeout accordingly.",
        ResultDescription = "success, outcome (Loading/NewColony/ModpackDiff/Failed/Cancelled), message, pin decision, registered; map tile once a save loaded")]
    public async Task<object> Connect(
        IRimBridgeContext ctx,
        [ToolParameter(Description = "Account login", Required = true)] string login,
        [ToolParameter(Description = "Account password (never echoed)", Required = true)] string password,
        [ToolParameter(Description = "Server host")] string host = "127.0.0.1",
        [ToolParameter(Description = "Server port")] int port = 25560,
        [ToolParameter(Description = "Display name of the server entry; defaults to the host")] string? name = null,
        [ToolParameter(Description = "Player name for a new account; defaults to the login")] string? playerName = null,
        [ToolParameter(Description = "auto: register when the login is refused; never: log in only")] string register = "auto",
        [ToolParameter(Description = "Join password of the server, when it has one")] string? serverPassword = null,
        [ToolParameter(Description = "Also trust a certificate that differs from the pinned one")] bool trustChangedPin = false,
        [ToolParameter(Description = "Seconds to wait for the login and, for an existing colony, the loaded map")] int timeoutSec = 120)
    {
        if (string.IsNullOrWhiteSpace(login) || string.IsNullOrEmpty(password)) return Bridge.Fail("login and password are required");
        if (register is not ("auto" or "never")) return Bridge.Fail("register must be auto or never");
        if (timeoutSec < 1) return Bridge.Fail("timeoutSec must be at least 1");
        if (!Bridge.TryEnterFlow("connect")) return Bridge.Fail($"{Bridge.RunningFlow} is still running");
        Task<LoginOutcome>? abandoned = null;
        try
        {
            var clock = Stopwatch.StartNew();
            var endpoint = new ServerEndpoint(host.Trim(), port, string.IsNullOrWhiteSpace(name) ? host.Trim() : name.Trim());
            var preset = new PresetLogin(login.Trim(), password, playerName, register == "auto", serverPassword);
            var pins = new PresetPinPrompt(trustChangedPin);

            var started = await Bridge.OnMain<object>(ctx, () =>
            {
                if (Current.ProgramState != ProgramState.Entry) return Bridge.Fail($"connect works from the main menu; the game is in {Current.ProgramState}");
                if (RimlinkSession.Active) return Bridge.Fail($"already connected to {RimlinkSession.Endpoint!.Display}");
                if (!RimlinkMod.Settings.Servers.Contains(endpoint.ToSettingsLine()))
                {
                    RimlinkMod.Settings.Servers.Add(endpoint.ToSettingsLine());
                    RimlinkMod.Settings.Write();
                }
                return LoginFlow.Run(endpoint, new GameCredentials(preset), pins);
            });
            if (started is not Task<LoginOutcome> flow) return started;

            if (await Task.WhenAny(flow, Task.Delay(TimeSpan.FromSeconds(timeoutSec))) != flow)
            {
                // The login keeps running; the guard stays until it ends, so no second login or new colony starts beside it.
                abandoned = flow;
                return Bridge.Fail($"login did not finish in {timeoutSec} s; it goes on, and connect/start_colony wait for it",
                    new { topWindow = await Bridge.OnMain(ctx, Bridge.TopWindowType) });
            }

            var outcome = flow.Result;
            bool success = outcome.Kind is LoginOutcomeKind.Loading or LoginOutcomeKind.NewColony;
            int? tile = null;
            if (outcome.Kind == LoginOutcomeKind.Loading)
            {
                int left = Math.Max(1, timeoutSec - (int)clock.Elapsed.TotalSeconds);
                if (!await WaitForMap(ctx, left))
                    return Bridge.Fail($"the save did not finish loading in {timeoutSec} s", new { outcome = outcome.Kind.ToString(), topWindow = await Bridge.OnMain(ctx, Bridge.TopWindowType) });
                tile = await Bridge.OnMain(ctx, () => (int)Find.CurrentMap.Tile);
            }
            return new
            {
                success,
                outcome = outcome.Kind.ToString(),
                message = outcome.Message,
                pin = pins.Decision,
                registered = success && preset.Attempts == 2,
                server = endpoint.Display,
                map = tile,
                nextStep = outcome.Kind == LoginOutcomeKind.NewColony ? "rimlink/start_colony" : null,
            };
        }
        finally
        {
            if (abandoned is not null) Bridge.Flows.ExitWhen(abandoned);
            else Bridge.ExitFlow();
        }
    }

    /// <summary>The game is running on a map and no long event (load, generation) is in progress.</summary>
    public static Task<bool> WaitForMap(IRimBridgeContext ctx, int timeoutSec) =>
        Bridge.WaitUntil(ctx, () => Current.ProgramState == ProgramState.Playing && Find.CurrentMap != null && !LongEventHandler.AnyEventNowOrWaiting, timeoutSec);
}
