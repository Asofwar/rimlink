using Microsoft.EntityFrameworkCore;
using Rimlink.Server.Application.Admin;
using Rimlink.Server.Application.Outposts;
using Xunit;

namespace Rimlink.Server.Tests.Outposts;

public sealed class OutpostAdminUninitializedInspectionTests
{
    [Fact]
    public async Task Positive_null_observation_is_stable_exact_and_never_initializes_or_changes_files()
    {
        using var f = new AuthorityHarness(); await Prepare(f);
        var before = OutpostAdminInspectionTests.Files(f); var startup = f.Feature.Startup;
        var service = OutpostAdminActivationTests.Service(f);
        var first = await service.InspectAsync(Actor.Api, default);
        var again = await service.InspectAsync(Actor.Api, default);
        Assert.Equal(OutpostAuthorityObservationKind.Uninitialized, first.Kind);
        Assert.NotNull(first.Uninitialized); Assert.Null(first.Committed); Assert.Null(first.Unavailable);
        Assert.Equal(OutpostAdminIntentCodec.ObservationDigest(first), OutpostAdminIntentCodec.ObservationDigest(again));
        Assert.Equal(before, OutpostAdminInspectionTests.Files(f)); Assert.Equal(startup, f.Feature.Startup);
        Assert.False(f.Feature.IsAvailable);
        var world = await f.Db.World.AsNoTracking().SingleAsync();
        Assert.Equal(world.InfluenceInstanceId, first.Uninitialized.World);
        Assert.Equal(OutpostInitializeQualification.WorldDigest(world, world.InfluenceInstanceId), first.Uninitialized.WorldBindingDigest);
        Assert.Equal(32, first.Uninitialized.QualificationDigest.Length);
        var schema = await OutpostDomainSchema.ValidateAsync(f.Db, OutpostSchemaContext.CurrentRuntime, default);
        Assert.Equal(schema.ProfileId, first.Uninitialized.Schema.ProfileId);
        Assert.Equal(schema.Digest.ToByteArray(), first.Uninitialized.Schema.Digest);
        Assert.Empty(await f.Db.OutpostAuthority.AsNoTracking().ToListAsync());
        Assert.Empty(await f.Db.OutpostTransitions.AsNoTracking().ToListAsync());
        Assert.Empty(await f.Db.OutpostAdminCommands.AsNoTracking().ToListAsync());
        Assert.Empty(await f.Db.AdminActions.AsNoTracking().ToListAsync());
    }

    [Theory]
    [InlineData("id")][InlineData("seed")][InlineData("params")][InlineData("npc-tiles")]
    [InlineData("created-at")][InlineData("instance")][InlineData("revision")]
    public async Task Null_world_binding_uses_the_full_actual_world_projection_not_a_seed_or_synthetic_default(string field)
    {
        using var f = new AuthorityHarness(); await Prepare(f);
        var service = OutpostAdminActivationTests.Service(f);
        var first = await service.InspectAsync(Actor.Api, default);
        Assert.NotNull(first.Uninitialized);
        if (field == "id") await f.Db.World.ExecuteUpdateAsync(s => s.SetProperty(w => w.Id, 2L));
        else
        {
            var world = await f.Db.World.SingleAsync();
            switch (field)
            {
                case "seed": world.Seed = "changed-seed"; break;
                case "params": world.Params = [4, 3, 2, 1]; break;
                case "npc-tiles": world.NpcTiles = "1,2,3"; break;
                case "created-at": world.CreatedAt = world.CreatedAt.AddTicks(1); break;
                case "instance": world.InfluenceInstanceId = "another-real-world"; break;
                case "revision": world.InfluenceRevision++; break;
            }
            await f.Db.SaveChangesAsync();
        }
        f.Db.ChangeTracker.Clear(); await f.Db.Database.CloseConnectionAsync();
        var before = OutpostAdminInspectionTests.Files(f);
        var next = await service.InspectAsync(Actor.Api, default);
        Assert.NotNull(next.Uninitialized); Assert.NotEqual(first.Uninitialized.WorldBindingDigest, next.Uninitialized.WorldBindingDigest);
        Assert.Equal(first.Uninitialized.QualificationDigest, next.Uninitialized.QualificationDigest);
        Assert.Equal(before, OutpostAdminInspectionTests.Files(f));
    }

    [Theory]
    [InlineData("protected-history")][InlineData("orphan-initial-audit")][InlineData("missing-world")][InlineData("missing-instance")][InlineData("unknown-schema")]
    public async Task Missing_or_corrupt_actual_facts_never_become_positive_uninitialized_observation(string problem)
    {
        using var f = new AuthorityHarness(); await Prepare(f);
        if (problem is "protected-history" or "orphan-initial-audit")
        {
            var intent = OutpostAdminIntentCodecTests.Example(OutpostAdminActionKind.Initialize);
            var evidence = OutpostAdminReceiptEvidence.Create(intent, new(1, intent.Allocation.ReceiptId,
                OutpostAdminDurableResultKind.AuthorityInitialized, null, null, [], true));
            if (problem == "protected-history") f.Db.OutpostAdminCommands.Add(evidence.Receipt);
            f.Db.AdminActions.Add(evidence.Audit);
            await f.Db.SaveChangesAsync();
        }
        if (problem == "missing-world") await f.Db.World.ExecuteDeleteAsync();
        if (problem == "missing-instance") await f.Db.World.ExecuteUpdateAsync(s => s.SetProperty(w => w.InfluenceInstanceId, ""));
        if (problem == "unknown-schema") await f.Db.Database.ExecuteSqlRawAsync("CREATE TABLE unexpected_null_projection(value INTEGER);");
        f.Db.ChangeTracker.Clear(); await f.Db.Database.CloseConnectionAsync();
        var before = OutpostAdminInspectionTests.Files(f);
        var actual = await OutpostAdminActivationTests.Service(f).InspectAsync(Actor.Api, default);
        Assert.Equal(OutpostAuthorityObservationKind.Unavailable, actual.Kind); Assert.Null(actual.Uninitialized);
        Assert.Equal(before, OutpostAdminInspectionTests.Files(f));
        Assert.Empty(await f.Db.OutpostAuthority.AsNoTracking().ToListAsync());
        Assert.Empty(await f.Db.OutpostTransitions.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Unrelated_audit_and_cold_journal_layout_do_not_become_physical_store_hash_identity()
    {
        using var f = new AuthorityHarness(); await Prepare(f);
        var service = OutpostAdminActivationTests.Service(f);
        var first = await service.InspectAsync(Actor.Api, default); Assert.NotNull(first.Uninitialized);
        f.Db.AdminActions.Add(new() { ActorName = "unrelated-admin", Action = "server.backup", Details = "unrelated diagnostic", At = DateTime.UtcNow });
        await f.Db.SaveChangesAsync(); f.Db.ChangeTracker.Clear(); await f.Db.Database.CloseConnectionAsync();
        using (var journal = new FileStream(f.Anchor.DatabasePath + "-journal", FileMode.Append, FileAccess.Write, FileShare.None))
            journal.Write(new byte[512]); // Header remains proven cold; this is separately authorized fixture setup, not GET.
        var before = OutpostAdminInspectionTests.Files(f);
        var next = await service.InspectAsync(Actor.Api, default); Assert.NotNull(next.Uninitialized);
        Assert.Equal(OutpostAdminIntentCodec.ObservationDigest(first), OutpostAdminIntentCodec.ObservationDigest(next));
        Assert.Equal(before, OutpostAdminInspectionTests.Files(f));
    }

    internal static async Task Prepare(AuthorityHarness f)
    {
        f.Anchor.Provision();
        f.Db.World.Add(new() { Id = 1, Seed = "null-fixture", Params = [1, 2, 3], NpcTiles = "",
            CreatedAt = new DateTime(2026, 11, 5, 0, 0, 0, DateTimeKind.Utc), InfluenceInstanceId = "world-1" });
        await f.Db.SaveChangesAsync(); f.Db.ChangeTracker.Clear();
        using (await f.Files.PerStoreLock.HoldAsync(default)) { }
        await OutpostMainObservationTests.Prepare(f, f.MainObservation);
    }
}
