using Rimlink.Protocol.V1;

namespace Rimlink.Client.Net;

/// <summary>One immutable snapshot at a time; a failed upload does not discard the following recovery snapshot.</summary>
public sealed class SaveUploadQueue
{
    private readonly object gate = new();
    private Task tail = Task.CompletedTask;
    private readonly List<SaveSnapshot> queued = [];
    public void PruneSnapshots(PendingUpload pending, IEnumerable<string?> protectedIds)
    {
        lock (gate) pending.PruneSnapshots(protectedIds.Concat(queued.Select(s => s.Id)));
    }
    public Task<SaveInfo> Enqueue(SaveSnapshot snapshot, Func<SaveSnapshot, CancellationToken, Task<SaveInfo>> send, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            var previous = tail;
            queued.Add(snapshot);
            var next = Task.Run(async () =>
            {
                try
                {
                    try { await previous.ConfigureAwait(false); } catch { }
                    cancellationToken.ThrowIfCancellationRequested();
                    return await send(snapshot, cancellationToken).ConfigureAwait(false);
                }
                finally { lock (gate) queued.Remove(snapshot); }
            });
            tail = next;
            return next;
        }
    }
}
