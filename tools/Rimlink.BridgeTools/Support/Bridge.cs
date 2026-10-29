using System.Diagnostics;
using RimBridgeServer.Sdk;
using Verse;

namespace Rimlink.BridgeTools;

/// <summary>Shared plumbing of the rimlink/* tools: main-thread access, frame-paced waits, failure results, one flow at a time.</summary>
public static class Bridge
{
    public static FlowGuard Flows { get; } = new();

    /// <summary>The connect/start_colony flow in progress, for the refusal text.</summary>
    public static string? RunningFlow => Flows.Running;

    /// <summary>Game objects are touched only here: whatever thread the bridge runs the tool on.</summary>
    public static Task<T> OnMain<T>(IRimBridgeContext ctx, Func<T> func) => ctx.MainThread.InvokeAsync(func);

    /// <summary>
    /// Checks the condition on the main thread once per frame until it holds or the time is up. Not RunUntilAsync:
    /// that one takes the bridge's game-control lease, which other tools may hold.
    /// </summary>
    public static async Task<bool> WaitUntil(IRimBridgeContext ctx, Func<bool> condition, int timeoutSec)
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            if (await OnMain(ctx, condition)) return true;
            if (clock.Elapsed.TotalSeconds > timeoutSec) return false;
            await ctx.Game.NextFrameAsync();
        }
    }

    public static object Fail(string error, object? details = null) => new { success = false, error, details };

    public static bool TryEnterFlow(string name) => Flows.TryEnter(name);

    public static void ExitFlow() => Flows.Exit();

    /// <summary>
    /// Main thread only. The topmost real window: <see cref="ImmediateWindow"/>s (tooltips, the time and
    /// readout overlays) sit above everything and would hide a dialog.
    /// </summary>
    public static Window? TopWindow() => Find.WindowStack?.Windows.LastOrDefault(w => w is not ImmediateWindow);

    /// <summary>Main thread only.</summary>
    public static string? TopWindowType() => TopWindow()?.GetType().Name;
}
