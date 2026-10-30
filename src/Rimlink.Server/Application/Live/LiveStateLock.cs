namespace Rimlink.Server.Application.Live;

/// <summary>
/// One state change of a live session at a time on this server. Every change reads the row, decides and writes, and they come from
/// everywhere at once (stream handlers, the sweep, RPCs, admins): without the lock the last writer overwrites a state it never saw,
/// so a lost host reads as Running, or an accept revives a session whose pawns already went home.
/// </summary>
public sealed class LiveStateLock
{
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task<IDisposable> Hold()
    {
        await gate.WaitAsync();
        return new Release(gate);
    }

    private sealed class Release(SemaphoreSlim gate) : IDisposable
    {
        private int released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref released, 1) == 0) gate.Release();
        }
    }
}
