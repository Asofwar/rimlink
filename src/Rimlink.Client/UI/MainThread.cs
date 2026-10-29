using System.Collections.Concurrent;
using HarmonyLib;
using Verse;

namespace Rimlink.Client.UI;

/// <summary>
/// Runs UI work on the game's main thread from network callbacks. Actions queue up and are drained once per frame
/// from <see cref="Root.Update"/> (entry and play scenes alike). LongEventHandler.ExecuteWhenFinished is not used here:
/// with no long event running it executes the action inline on the calling thread.
/// </summary>
public static class MainThread
{
    private const int MaxPerFrame = 64;
    private static readonly ConcurrentQueue<Action> queue = new();

    public static void Post(Action action) => queue.Enqueue(action);

    public static Task<T> Ask<T>(Func<TaskCompletionSource<T>, Window> createDialog)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() => Find.WindowStack.Add(createDialog(tcs)));
        return tcs.Task;
    }

    /// <summary>A value computed on the main thread (a translated text), for a background flow that awaits it; never block on it there.</summary>
    public static Task<T> Get<T>(Func<T> read)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() =>
        {
            try { tcs.SetResult(read()); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        return tcs.Task;
    }

    /// <summary>Main thread only.</summary>
    public static void Drain()
    {
        for (int i = 0; i < MaxPerFrame && queue.TryDequeue(out var action); i++)
        {
            try { action(); }
            catch (Exception ex) { Log.Error("[Rimlink] main-thread action failed: " + ex); }
        }
    }
}

[HarmonyPatch(typeof(Root), nameof(Root.Update))]
public static class Patch_Root_Update
{
    public static void Postfix() => MainThread.Drain();
}
