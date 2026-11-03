using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Rimlink.Protocol.ModpackDelivery;
using Rimlink.Server.Hosting;

namespace Rimlink.Server.Application.Modpack;

/// <summary>Process-local download credentials and leases; no token is persisted or logged.</summary>
public sealed class DeliveryRuntime : IDisposable
{
    public SemaphoreSlim CatalogLock { get; } = new(1);
    public SemaphoreSlim UploadLock { get; } = new(1);
    private readonly object sync = new();
    private readonly Dictionary<string, Grant> grants = [];
    private readonly Dictionary<string, (DateTimeOffset Start, int Count)> attempts = [];
    private readonly TimeProvider clock;
    private readonly IDisposable? serverChanges, authChanges;
    private int transfers;
    private long epoch;
    public long Epoch { get { lock (sync) return epoch; } }
    public bool HasTransfers { get { lock (sync) return transfers > 0; } }
    private readonly Dictionary<string, (long Size, DateTime Stamp, string Hash)> verifiedArchives = [];
    public bool ArchiveMatches(string path, string expectedHash, long expectedSize)
    {
        lock (sync)
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length != expectedSize) return false;
            if (verifiedArchives.TryGetValue(path, out var known) && known.Size == info.Length && known.Stamp == info.LastWriteTimeUtc && known.Hash == expectedHash) return true;
            using var file = File.OpenRead(path);
            if (Convert.ToHexStringLower(SHA256.HashData(file)) != expectedHash) return false;
            if (verifiedArchives.Count >= 4) verifiedArchives.Clear();
            verifiedArchives[path] = (info.Length, info.LastWriteTimeUtc, expectedHash); return true;
        }
    }

    public DeliveryRuntime(TimeProvider clock, IOptionsMonitor<ServerOptions> server, IOptionsMonitor<AuthOptions> auth)
    {
        this.clock = clock;
        serverChanges = server.OnChange((_, _) => Revoke()); authChanges = auth.OnChange((_, _) => Revoke());
    }

    public sealed class Grant(string key, string package, string catalog, DateTimeOffset until)
    {
        public string Key { get; } = key;
        public string Package { get; } = package;
        public string Catalog { get; } = catalog;
        public DateTimeOffset Until { get; } = until;
        public CancellationTokenSource Cancellation { get; } = new();
        public bool Transferring { get; set; }
    }
    private static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    public void Attempt(string ip)
    {
        lock (sync)
        {
            var now = clock.GetUtcNow();
            foreach (var key in attempts.Where(p => now - p.Value.Start >= TimeSpan.FromMinutes(1)).Select(p => p.Key).ToArray()) attempts.Remove(key);
            if (!attempts.TryGetValue(ip, out var value))
            {
                if (attempts.Count >= 4096) throw new DeliveryException("Busy", retryable: true);
                value = (now, 0);
            }
            attempts[ip] = (value.Start, value.Count + 1);
            if (value.Count >= DeliveryLimits.AttemptsPerMinute) throw new DeliveryException("RateLimit", retryable: true);
        }
    }
    public AccessGrant Issue(PackageManifest manifest, long expectedEpoch)
    {
        lock (sync)
        {
            Expire();
            if (expectedEpoch != epoch) throw new DeliveryException("StaleCatalog");
            if (grants.Count >= DeliveryLimits.MaxGrants) throw new DeliveryException("Busy", retryable: true);
            string token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
            var grant = new Grant(Hash(token), manifest.PackageId, manifest.CatalogHash, clock.GetUtcNow() + DeliveryLimits.AccessLifetime);
            grants.Add(grant.Key, grant);
            return new AccessGrant(token, grant.Until.UtcDateTime, grant.Package, manifest.CollectionRevision, grant.Catalog);
        }
    }
    public Grant Find(string token, string package)
    {
        lock (sync)
        {
            Expire();
            if (token.Length != 64 || !grants.TryGetValue(Hash(token), out var grant) || grant.Package != package || grant.Cancellation.IsCancellationRequested) throw new DeliveryException("Unauthorized");
            return grant;
        }
    }
    public IDisposable Transfer(Grant grant)
    {
        lock (sync)
        {
            Check(grant);
            if (transfers >= DeliveryLimits.MaxTransfers || grant.Transferring) throw new DeliveryException("Busy", retryable: true);
            transfers++; grant.Transferring = true;
            return new TransferLease(this, grant);
        }
    }
    public void Check(Grant grant)
    {
        if (clock.GetUtcNow() >= grant.Until || grant.Cancellation.IsCancellationRequested) throw new DeliveryException("Unauthorized");
    }
    public void Revoke()
    {
        lock (sync) { epoch++; foreach (var g in grants.Values) g.Cancellation.Cancel(); grants.Clear(); }
    }
    public TimeSpan TimeLeft(Grant grant) => grant.Until > clock.GetUtcNow() ? grant.Until - clock.GetUtcNow() : TimeSpan.Zero;
    private void Expire()
    {
        foreach (var key in grants.Where(p => clock.GetUtcNow() >= p.Value.Until).Select(p => p.Key).ToArray()) { grants[key].Cancellation.Cancel(); grants.Remove(key); }
    }
    private sealed class TransferLease(DeliveryRuntime owner, Grant grant) : IDisposable
    {
        private bool disposed;
        public void Dispose() { lock (owner.sync) { if (disposed) return; disposed = true; owner.transfers--; grant.Transferring = false; } }
    }
    public void Dispose() { serverChanges?.Dispose(); authChanges?.Dispose(); Revoke(); CatalogLock.Dispose(); UploadLock.Dispose(); }
}
