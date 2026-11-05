using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Storage.Entities;
using Xunit;

namespace Rimlink.Server.Tests.Outposts;

public sealed class OutpostFreshFenceTests
{
    [Theory]
    [InlineData(false, false)] [InlineData(true, false)]
    [InlineData(false, true)] [InlineData(true, true)]
    public async Task Fresh_or_aborted_fence_is_never_empty_initialized_or_a_committed_head(bool history, bool backedOut)
    {
        using var h = new AuthorityHarness();
        h.Anchor.Provision();
        if (history)
        {
            h.Db.Outposts.Add(new OutpostRow { Id = "retained-history", State = "Closed" });
            await h.Db.SaveChangesAsync();
        }
        var fresh = Fence() with { State = backedOut ? FreshFenceState.BackedOutEmpty : FreshFenceState.RestoreInProgress };
        using (var held = await h.Anchor.AcquireAsync(CancellationToken.None))
        {
            h.Anchor.WriteEnvelopeDurable(held, fresh, CancellationToken.None);
            Assert.Equal(fresh, h.Anchor.ReadEnvelope(held));
            Assert.False(h.Anchor.IsPhysicallyUninitialized(held));
            Assert.Throws<InvalidDataException>(() => h.Anchor.Read(held));
        }
        byte[] before = File.ReadAllBytes(h.Anchor.DatabasePath);
        var startup = await h.Authority.OpenAsync(CancellationToken.None);
        Assert.Equal(new OutpostStartupResult(OutpostStartupState.Maintenance, "anchor-fresh-fence-requires-restore"), startup);
        var initialization = await h.Authority.InitializeAsync(new("world-1", "test-admin", true), CancellationToken.None);
        Assert.Equal(new OutpostStartupResult(OutpostStartupState.Maintenance,
            history ? "initialization-history-not-empty" : "initialization-anchor-not-empty"), initialization);
        Assert.Empty(await h.Db.OutpostAuthority.AsNoTracking().ToListAsync());
        Assert.Empty(await h.Db.OutpostTransitions.AsNoTracking().ToListAsync());
        Assert.Equal(before, File.ReadAllBytes(h.Anchor.DatabasePath));
        using var reread = await h.Anchor.AcquireAsync(CancellationToken.None);
        Assert.Equal(fresh, h.Anchor.ReadEnvelope(reread));
        Assert.False(h.Feature.IsAvailable);
    }

    [Fact]
    public async Task Qualified_null_store_is_distinct_from_fresh_and_known_records_keep_version_one()
    {
        using var h = new AuthorityHarness(); h.Anchor.Provision();
        using var held = await h.Anchor.AcquireAsync(CancellationToken.None);
        Assert.Null(h.Anchor.ReadEnvelope(held));
        Assert.True(h.Anchor.IsPhysicallyUninitialized(held));
        var known = new AnchorRecord(1, Guid.NewGuid().ToString("D"), "world-1", Guid.NewGuid().ToString("D"),
            new(Guid.NewGuid().ToString("D"), ByteString.CopyFrom(new byte[32])));
        h.Anchor.WriteDurable(held, known, CancellationToken.None);
        Assert.Equal(new KnownAnchorEnvelope(known), h.Anchor.ReadEnvelope(held));
        Assert.Equal(known, h.Anchor.Read(held));
        Assert.False(h.Anchor.IsPhysicallyUninitialized(held));
    }

    private static FreshAnchorFence Fence() => new(FreshFenceState.RestoreInProgress, FreshFenceMode.ImportNewHost,
        PreviousWitnessKind.AbsentQualifiedEmpty, Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"),
        Guid.NewGuid().ToString("D"), "world-1", Guid.NewGuid().ToString("D"), ByteString.CopyFrom(new byte[32]));
}
