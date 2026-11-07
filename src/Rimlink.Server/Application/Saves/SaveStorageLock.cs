namespace Rimlink.Server.Application.Saves;

/// <summary>Serializes save mutations with a CLI backup in another process. The lock file must never be deleted.</summary>
public sealed class SaveStorageLock(string dataDir)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private FileStream? lease;

    public Task WaitAsync(CancellationToken cancellationToken = default) => WaitCoreAsync(create: true, cancellationToken);

    private async Task WaitCoreAsync(bool create, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            while ((lease = TryOpen(dataDir, create)) is null)
                await Task.Delay(25, cancellationToken);
        }
        catch
        {
            gate.Release();
            throw;
        }
    }

    public void Release()
    {
        lease!.Dispose();
        lease = null;
        gate.Release();
    }

    public async Task<IDisposable> HoldAsync(CancellationToken cancellationToken = default)
    {
        await WaitAsync(cancellationToken);
        return new Held(this);
    }

    // Read-only inspection must not create a missing fence as a side effect of GET.
    internal async Task<IDisposable> HoldExistingAsync(CancellationToken cancellationToken = default)
    {
        await WaitCoreAsync(create: false, cancellationToken);
        return new Held(this);
    }

    public static IDisposable Hold(string dataDir)
    {
        while (true)
        {
            if (TryOpen(dataDir) is { } held) return held;
            Thread.Sleep(25);
        }
    }

    private static FileStream? TryOpen(string dataDir, bool create = true)
    {
        try
        {
            string path = Path.Combine(dataDir, "save-storage.lock");
            if (!create && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("inspection-lock-path-link");
            return new FileStream(path, create ? FileMode.OpenOrCreate : FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException ex) when ((ex.HResult & 0xFFFF) is 11 or 32 or 33)
        {
            return null; // Unix EWOULDBLOCK or Windows sharing/lock violation; other I/O errors must surface.
        }
    }

    private sealed class Held(SaveStorageLock owner) : IDisposable
    {
        private SaveStorageLock? heldOwner = owner;

        public void Dispose() => Interlocked.Exchange(ref heldOwner, null)?.Release();
    }
}
