using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Rimlink.Server.Application.Procurement;
using Rimlink.Server.Storage;
using Rimlink.Server.Storage.Entities;
using Xunit;
namespace Rimlink.Server.Tests.Procurement;

public class ProcurementSchemaTests
{
    [Fact]
    public async Task UpgradeFromBasePreservesWalletAndCreatesFeatureTablesAndUniqueRequestScope()
    {
        using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        using var db = new RimlinkDbContext(new DbContextOptionsBuilder<RimlinkDbContext>().UseSqlite(connection).Options);
        await db.GetService<IMigrator>().MigrateAsync("20261030085555_IncidentRequestIdPerBuyer");
        db.Wallets.Add(new Wallet { PlayerId = 0, Balance = 123, UpdatedAt = DateTime.UtcNow }); await db.SaveChangesAsync();
        await db.Database.MigrateAsync();
        Assert.Equal(123, (await db.Wallets.SingleAsync()).Balance);
        Assert.Empty(await db.Set<ProcurementOrder>().ToListAsync());
        Assert.Empty(await db.Set<ProcurementDelivery>().ToListAsync());
        Assert.Empty(await db.Set<ProcurementRequest>().ToListAsync());
        var key = db.Model.FindEntityType(typeof(ProcurementRequest))!.GetIndexes().Single(x => x.IsUnique);
        Assert.Equal(new[] { "PlayerId", "RequestId" }, key.Properties.Select(p => p.Name));
        Assert.False(db.Database.HasPendingModelChanges());
    }

    [Fact]
    public void EveryFeatureRefusalHasEnglishAndRussianStrings()
    {
        var keys = typeof(ProcurementRefusalKeys).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Select(f => "Rimlink.Refusal." + (string)f.GetRawConstantValue()!).ToArray();
        foreach (string language in new[] { "English", "Russian" })
        {
            var entries = System.Xml.Linq.XDocument.Load(Path.Combine(Support.RepoPaths.Root, "mod", "Languages", language, "Keyed", "Procurement.xml")).Root!.Elements().ToDictionary(e => e.Name.LocalName, e => e.Value);
            foreach (string key in keys) Assert.True(entries.TryGetValue(key, out string? text) && !string.IsNullOrWhiteSpace(text), key + " missing in " + language);
        }
    }
}
