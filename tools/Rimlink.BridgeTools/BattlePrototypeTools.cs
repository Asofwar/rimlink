using RimBridgeServer.Sdk;
using Rimlink.BattlePrototype;
using Rimlink.BridgeTools.BattlePrototype;
using Rimlink.Client.Game;
using Verse;

namespace Rimlink.BridgeTools;

public sealed class BattlePrototypeTools
{
    [Tool("rimlink/battle_bind", Description = "Bind this exact isolated game to a local dev-only battle role; no player session allowed")]
    public async Task<object> Bind(IRimBridgeContext ctx,
        [ToolParameter(Description = "Exact absolute role JSON under the owned stand battles directory", Required = true)] string bindingFile)
    {
        try
        {
            var state = await Bridge.OnMain(ctx, () => (session: RimlinkSession.Active || RimlinkSession.Connection is not null, savedata: GenFilePaths.SaveDataFolderPath));
            var runtime = await BattleRuntime.Bind(bindingFile, state.session, state.savedata);
            return await Bridge.OnMain<object>(ctx, () => { runtime.Activate(); return runtime.Status(); });
        }
        catch (Exception ex) { return Bridge.Fail(ex.Message); }
    }

    [Tool("rimlink/battle_status", Description = "Read dev-only battle state, full authoritative view and fixture receipt")]
    public Task<object> Status(IRimBridgeContext ctx) => Bridge.OnMain(ctx, () => BattleRuntime.Current?.Status() ?? new { success = true, phase = "Unbound", hasSession = RimlinkSession.Active });

    [Tool("rimlink/battle_prepare", Description = "Arbiter only: load the verified disposable template, create eight equal fighters and save/reload the native initial snapshot")]
    public async Task<object> Prepare(IRimBridgeContext ctx)
    {
        try
        {
            var runtime = await Bridge.OnMain(ctx, () => BattleRuntime.Current ?? throw new InvalidDataException("not bound"));
            await Bridge.OnMain(ctx, () => { runtime.BeginPrepare(); return true; });
            if (!await Loaded(ctx)) throw new TimeoutException("template load did not finish");
            await Bridge.OnMain(ctx, () => { runtime.BuildArena(); return true; });
            if (!await Loaded(ctx)) throw new TimeoutException("initial snapshot reload did not finish");
            return await Bridge.OnMain<object>(ctx, () => { runtime.FinishPrepare(); return runtime.Status(); });
        }
        catch (Exception ex)
        {
            return await Bridge.OnMain(ctx, () => { BattleRuntime.Current?.Abort(BattleEndReason.InvalidState, ex.Message); return Bridge.Fail(ex.Message); });
        }
    }
    private static Task<bool> Loaded(IRimBridgeContext ctx) => Bridge.WaitUntil(ctx,
        () => Current.ProgramState == ProgramState.Playing && Current.Game is { PlayerHasControl: true }
            && Find.CurrentMap is not null && !LongEventHandler.AnyEventNowOrWaiting, 120);

    [Tool("rimlink/battle_start", Description = "Commit the verified initial snapshot to the dev coordinator and start the arbiter tick source")]
    public async Task<object> Start(IRimBridgeContext ctx)
    {
        try
        {
            var operation = await Bridge.OnMain(ctx, () => (BattleRuntime.Current ?? throw new InvalidDataException("not bound")).Start());
            await operation;
            if (!await Bridge.WaitUntil(ctx, () => BattleRuntime.Current is { Running: true } or { Phase: "RecoveryRequired" }, 10))
                return Bridge.Fail("arbiter start did not reach a running game");
            return await Status(ctx);
        }
        catch (Exception ex) { return Bridge.Fail(ex.Message); }
    }

    [Tool("rimlink/battle_view", Description = "Open this side's native debug tactical view; movement and attacks go through this process's role-bound channel")]
    public Task<object> View(IRimBridgeContext ctx) => Bridge.OnMain<object>(ctx, () =>
    {
        var runtime = BattleRuntime.Current;
        if (runtime is null || runtime.Binding.Role == BattleRole.Arbiter) return Bridge.Fail("a bound external side controller is required");
        if (!Find.WindowStack.Windows.OfType<BattleViewWindow>().Any())
            Find.WindowStack.Add(new BattleViewWindow(runtime) { doCloseX = true, absorbInputAroundWindow = false });
        return runtime.Status();
    });

    [Tool("rimlink/battle_stop", Description = "Stop this own dev-only battle binding, pause the arbiter and remove only its scoped hooks")]
    public Task<object> Stop(IRimBridgeContext ctx) => Bridge.OnMain<object>(ctx, () =>
    {
        foreach (var window in Find.WindowStack.Windows.OfType<BattleViewWindow>().ToArray()) window.Close();
        BattleRuntime.Current?.Stop(); return new { success = true, phase = "Unbound" };
    });
}
