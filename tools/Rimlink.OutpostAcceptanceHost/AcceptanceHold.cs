namespace Rimlink.OutpostAcceptanceHost;

internal sealed record AcceptanceFaultTarget(string SessionSha256, string ScopeSha256, string OperationId, string Kind, string IntentSha256 = "");
internal sealed record AcceptanceHoldState(string Boundary, AcceptanceFaultTarget Target, string State);

// A one-shot wait only; callers prove ownership and revalidate their actual session on both sides.
internal sealed class AcceptanceHold
{
    private readonly object gate = new();
    private AcceptanceHoldState? state;
    private TaskCompletionSource<bool>? release;
    internal AcceptanceHoldState? Observe() { lock (gate) return state; }
    internal void Arm(string boundary, AcceptanceFaultTarget target)
    {
        lock (gate)
        {
            if (state?.State is "Armed" or "Reached" || string.IsNullOrWhiteSpace(boundary)
                || !Hash(target.SessionSha256) || !Hash(target.ScopeSha256) || string.IsNullOrWhiteSpace(target.Kind)
                || target.OperationId.Length > 128 || target.IntentSha256.Length > 0 && !Hash(target.IntentSha256)) throw new InvalidDataException("acceptance-hold-arm-invalid");
            state = new(boundary, target, "Armed"); release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }
    internal async Task<bool?> ReachAsync(string boundary, AcceptanceFaultTarget actual, CancellationToken ct)
    {
        Task<bool> wait;
        lock (gate)
        {
            if (state is not { State: "Armed" } armed || armed.Boundary != boundary
                || armed.Target.SessionSha256 != actual.SessionSha256 || armed.Target.ScopeSha256 != actual.ScopeSha256
                || armed.Target.Kind != actual.Kind || armed.Target.OperationId.Length > 0 && armed.Target.OperationId != actual.OperationId
                || armed.Target.IntentSha256.Length > 0 && armed.Target.IntentSha256 != actual.IntentSha256)
                return null;
            if (actual.OperationId.Length is < 1 or > 128) throw new InvalidDataException("acceptance-hold-actual-operation-invalid");
            state = armed with { Target = actual, State = "Reached" }; wait = release!.Task;
        }
        try { return await wait.WaitAsync(ct); }
        catch (OperationCanceledException)
        {
            lock (gate) if (release?.Task == wait) { state = state! with { State = "Cancelled" }; release.TrySetCanceled(ct); }
            throw;
        }
    }
    internal void Release(AcceptanceFaultTarget target, bool drop)
    {
        lock (gate)
        {
            if (state is not { State: "Reached" } actual || actual.Target != target) throw new InvalidDataException("acceptance-hold-release-mismatch");
            state = actual with { State = drop ? "Dropped" : "Released" }; release!.TrySetResult(drop);
        }
    }
    internal void Disarm()
    {
        lock (gate)
        {
            if (state?.State is "Armed" or "Reached") { state = state with { State = "Cancelled" }; release!.TrySetCanceled(); }
        }
    }
    private static bool Hash(string value) => value.Length == 64 && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}
