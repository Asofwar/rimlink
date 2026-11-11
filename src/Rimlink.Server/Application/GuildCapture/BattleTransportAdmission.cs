using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Application.Saves;
using Rimlink.Server.Storage;

namespace Rimlink.Server.Application.GuildCapture;

// V1 has one physical executor slot, so this one lock covers its only active battle, including financial finalization.
internal sealed class BattleStateLock
{
    private readonly SemaphoreSlim gate = new(1, 1);
    internal async Task<IDisposable> HoldAsync(CancellationToken ct)
    { await gate.WaitAsync(ct); return new Release(gate); }
    private sealed class Release(SemaphoreSlim gate) : IDisposable
    {
        private SemaphoreSlim? held = gate;
        public void Dispose() => Interlocked.Exchange(ref held, null)?.Release();
    }
}

/// <summary>S → battle lock → short-lived read-only DB. No A acquisition or financial digest belongs to the hot path.</summary>
internal sealed class BattleTransportAdmission(DataDirectory data, SaveFiles files, OutpostAuthority authority, OutpostFeature feature, BattleStateLock battles)
{
    internal async Task<Lease> HoldAsync(CancellationToken ct,GuildRuntimeUse use=GuildRuntimeUse.Transport)
    {
        if (data.Root != files.DataRoot) throw new InvalidOperationException("battle transport storage fence belongs to another data directory");
        IDisposable? storage = null, battle = null; RimlinkDbContext? reader = null;
        try
        {
            storage = await files.PerStoreLock.HoldAsync(ct);
            battle = await battles.HoldAsync(ct);
            var cut = feature.GuildCut ?? throw Unavailable(); Require(cut,use);
            var connection = new SqliteConnectionStringBuilder { DataSource = data.DatabasePath, Mode = SqliteOpenMode.ReadOnly,
                Cache = SqliteCacheMode.Private, Pooling = false };
            reader = new(new DbContextOptionsBuilder<RimlinkDbContext>().UseSqlite(connection.ToString()).Options);
            await reader.Database.OpenConnectionAsync(ct);
            var head = await reader.OutpostAuthority.AsNoTracking().SingleOrDefaultAsync(ct);
            if (head is null || head.Id != 1 || head.Phase != "Committed" || head.WorldId != cut.WorldId || head.RestoreBoundary != cut.Boundary
                || head.HeadId != cut.HeadId || !head.HeadDigest.AsSpan().SequenceEqual(cut.HeadDigest.Span)) throw Unavailable();
            Require(cut,use);
            return new(this, cut, reader, battle, storage,use);
        }
        catch
        {
            try { if (reader is not null) await reader.DisposeAsync(); }
            finally { try { battle?.Dispose(); } finally { storage?.Dispose(); } }
            throw;
        }
    }

    private void Require(GuildVerifiedCut cut,GuildRuntimeUse use)
    {
        authority.RequireRuntimeGuild(use);
        if (feature.Startup.State != OutpostStartupState.Ready || !ReferenceEquals(feature.GuildCut, cut)) throw Unavailable();
    }
    private static OutpostEvidenceUnavailableException Unavailable() => new("battle-transport-cut-unavailable");

    internal sealed class Lease : IAsyncDisposable
    {
        private readonly BattleTransportAdmission owner;
        private readonly GuildRuntimeUse use;
        private RimlinkDbContext? reader;
        private IDisposable? battle, storage;
        internal GuildVerifiedCut Cut { get; }
        internal RimlinkDbContext Db
        {
            get
            {
                ObjectDisposedException.ThrowIf(reader is null, this);
                owner.Require(Cut,use); return reader;
            }
        }
        internal Lease(BattleTransportAdmission owner, GuildVerifiedCut cut, RimlinkDbContext reader, IDisposable battle, IDisposable storage,GuildRuntimeUse use)
        { this.owner = owner;this.use=use; Cut = cut; this.reader = reader; this.battle = battle; this.storage = storage; }
        public async ValueTask DisposeAsync()
        {
            var owned = Interlocked.Exchange(ref reader, null);
            try { if (owned is not null) await owned.DisposeAsync(); }
            finally
            {
                try { Interlocked.Exchange(ref battle, null)?.Dispose(); }
                finally { Interlocked.Exchange(ref storage, null)?.Dispose(); }
            }
        }
    }
}
