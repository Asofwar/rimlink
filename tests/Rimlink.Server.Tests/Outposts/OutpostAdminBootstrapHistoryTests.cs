using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Storage.Entities;
using Xunit;

namespace Rimlink.Server.Tests.Outposts;

public sealed class OutpostAdminBootstrapHistoryTests
{
    [Fact]
    public async Task Historical_empty_v1_schema_is_classified_without_a_v2_table_reader_or_ddl()
    {
        using var f = new AuthorityHarness();
        await f.Db.GetService<IMigrator>().MigrateAsync("20261104093958_OutpostOrdinaryReturns");
        await f.Db.Database.ExecuteSqlRawAsync("PRAGMA query_only=ON;");
        var schema = await OutpostDomainSchema.ValidateAsync(f.Db, OutpostSchemaContext.HistoricalCutV1, default);
        Assert.False(schema.IsCurrent);
        Assert.False(await f.Authority.HasHistoryAsync(default));
        Assert.Equal(schema, await OutpostDomainSchema.ValidateAsync(f.Db, OutpostSchemaContext.HistoricalCutV1, default));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Orphan_admin_receipt_is_protected_history_not_an_empty_installation(bool initialize)
    {
        using var f = new AuthorityHarness();
        f.Anchor.Provision();
        f.Db.World.Add(new() { Id = 1, Seed = "admin-history", InfluenceInstanceId = "world-1" });
        var command = OutpostAdminIntentCodecTests.Example(OutpostAdminActionKind.Initialize);
        var result = new OutpostAdminDurableResult(1, command.Allocation.ReceiptId,
            OutpostAdminDurableResultKind.AuthorityInitialized, null, null, [], true);
        var receipt = OutpostAdminReceiptEvidence.Create(command, result);
        // Deliberate corrupted partial history: schema-valid receipt, but no authority/transition.
        f.Db.OutpostAdminCommands.Add(receipt.Receipt);
        f.Db.AdminActions.Add(receipt.Audit);
        await f.Db.SaveChangesAsync();
        Assert.Empty(await f.Db.OutpostTransitions.AsNoTracking().ToListAsync());
        Assert.Empty(await f.Db.OutpostAuthority.AsNoTracking().ToListAsync());
        Assert.True(await f.Authority.HasHistoryAsync(default));
        var observed = initialize
            ? await f.Authority.InitializeAsync(new AuthorityInitialization("world-1", "must refuse orphan history", true), default)
            : await f.Authority.OpenAsync(default);
        Assert.Equal(OutpostStartupState.Maintenance, observed.State);
        Assert.Empty(await f.Db.OutpostTransitions.AsNoTracking().ToListAsync());
        Assert.Empty(await f.Db.OutpostAuthority.AsNoTracking().ToListAsync());
        Assert.Single(await f.Db.OutpostAdminCommands.AsNoTracking().ToListAsync());
        using var held = await f.Anchor.AcquireAsync(default);
        Assert.Null(f.Anchor.ReadEnvelope(held));
    }
}
