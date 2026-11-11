using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Storage;
using Xunit;

namespace Rimlink.Server.Tests.GuildCapture;

public sealed class GuildSchemaCatalogTests
{
    [Theory]
    [InlineData("v3-crlf-crlf-crlf")][InlineData("v3-crlf-lf-crlf")]
    [InlineData("v3-lf-crlf-crlf")][InlineData("v3-lf-lf-crlf")]
    [InlineData("v3-crlf-crlf-lf")][InlineData("v3-crlf-lf-lf")]
    [InlineData("v3-lf-crlf-lf")][InlineData("v3-lf-lf-lf")]
    public async Task Every_exact_cross_executor_catalog_is_read_without_rewriting_it(string profile)
    {
        using var resource = typeof(OutpostDomainSchema).Assembly.GetManifestResourceStream("Rimlink.OutpostSchema." + profile + ".json");
        Assert.NotNull(resource); using var descriptor = await JsonDocument.ParseAsync(resource);
        var objects = descriptor.RootElement.GetProperty("Objects").EnumerateArray()
            .Select(r => r.EnumerateArray().Select(v => v.ValueKind == JsonValueKind.Null ? null : v.GetString()).ToArray()).ToArray();
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        foreach (var row in objects.OrderBy(r => r[0] == "table" ? 0 : 1))
        {
            if (row[3] is null || row[1]!.StartsWith("sqlite_", StringComparison.Ordinal)) continue;
            using var command = connection.CreateCommand(); command.CommandText = row[3]; await command.ExecuteNonQueryAsync();
        }
        foreach (var migration in descriptor.RootElement.GetProperty("Migrations").EnumerateArray())
        {
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO __EFMigrationsHistory(MigrationId,ProductVersion) VALUES($id,'10.0.14')";
            command.Parameters.AddWithValue("$id", migration.GetString()); await command.ExecuteNonQueryAsync();
        }
        await using var db = new RimlinkDbContext(new DbContextOptionsBuilder<RimlinkDbContext>().UseSqlite(connection).Options);
        await db.Database.ExecuteSqlRawAsync("PRAGMA query_only=ON");
        var admitted = await OutpostDomainSchema.ValidateAsync(db, OutpostSchemaContext.HistoricalCutV3, default);
        Assert.Equal("rimlink-outposts-schema/" + profile, admitted.ProfileId);
        Assert.True(admitted.IsCurrent);
        Assert.True((await OutpostDomainSchema.ValidateAsync(db, OutpostSchemaContext.HistoricalCutV2, default)).IsCurrent);
    }
}
