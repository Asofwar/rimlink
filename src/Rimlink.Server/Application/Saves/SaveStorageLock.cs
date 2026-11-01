namespace Rimlink.Server.Application.Saves;

/// <summary>Serializes save mutations with a CLI backup in another process. The lock file must never be deleted.</summary>
public sealed class SaveStorageLock(string dataDir)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private FileStream? lease;

    public async Task WaitAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            while ((lease = TryOpen(dataDir)) is null)
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

    public async Task<IDisposable> HoldAsync()
    {
        await WaitAsync();
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

    private static FileStream? TryOpen(string dataDir)
    {
        try
        {
            return new FileStream(Path.Combine(dataDir, "save-storage.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException ex) when ((ex.HResult & 0xFFFF) is 11 or 32 or 33)
        {
            return null; // Unix EWOULDBLOCK or Windows sharing/lock violation; other I/O errors must surface.
        }
    }

    private sealed class Held(SaveStorageLock owner) : IDisposable
    {
        public void Dispose() => owner.Release();
    }
}
