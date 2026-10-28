using Verse;

namespace Rimlink.Client.UI;

/// <summary>Runs UI work on the game's main thread from network callbacks.</summary>
public static class MainThread
{
    public static void Post(Action action) => LongEventHandler.ExecuteWhenFinished(action);

    public static Task<T> Ask<T>(Func<TaskCompletionSource<T>, Window> createDialog)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() => Find.WindowStack.Add(createDialog(tcs)));
        return tcs.Task;
    }
}
