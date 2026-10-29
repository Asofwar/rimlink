namespace Rimlink.BridgeTools;

/// <summary>One connect/start_colony flow at a time. Game-free: unit-tested by link.</summary>
public sealed class FlowGuard
{
    private int held;

    /// <summary>The flow in progress, for the refusal text.</summary>
    public string? Running { get; private set; }

    public bool TryEnter(string name)
    {
        if (Interlocked.CompareExchange(ref held, 1, 0) != 0) return false;
        Running = name;
        return true;
    }

    public void Exit()
    {
        Running = null;
        Interlocked.Exchange(ref held, 0);
    }

    /// <summary>
    /// Keeps the guard until <paramref name="work"/> ends: a caller that timed out leaves while the flow it started
    /// still runs, and a second login next to it would fight it for the one session.
    /// </summary>
    public void ExitWhen(Task work) => work.ContinueWith(_ => Exit(), TaskContinuationOptions.ExecuteSynchronously);
}
