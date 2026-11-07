using System.Security.Cryptography;
using System.Text.Json;
using Rimlink.Server.Application.Outposts;
using Xunit;

namespace Rimlink.Server.Tests.Backup;

public sealed class OutpostRestoreRuntimeQualificationTests
{
    private const string Legacy = "rimlink-outposts-schema-20261105-0520";
    private const string Runtime14 = "rimlink-outposts-schema-20261107-runtime14";

    [Theory]
    [InlineData(".NET 10.0.13", Legacy, "win32")]
    [InlineData(".NET 10.0.13", Legacy, "unix")]
    [InlineData(".NET 10.0.14", Runtime14, "win32")]
    [InlineData(".NET 10.0.14", Runtime14, "unix")]
    public void Recorded_step_binds_framework_to_its_own_qualification_and_provider_profile(string framework, string qualification, string vfs)
    {
        var step = Step(framework, qualification, qualification + "/provider-v1", vfs);
        OutpostRestoreSchemaMigration.ExecutionProbe.Value = operation => throw new InvalidOperationException("historical validation must not execute: " + operation);
        try { OutpostRestoreSchemaMigration.ValidateRecordedStep(step); }
        finally { OutpostRestoreSchemaMigration.ExecutionProbe.Value = null; }
    }

    [Theory]
    [InlineData(".NET 10.0.14", Legacy, Legacy)]
    [InlineData(".NET 10.0.13", Runtime14, Runtime14)]
    [InlineData(".NET 10.0.14", Runtime14, Legacy)]
    [InlineData(".NET 10.0.13", Legacy, Runtime14)]
    [InlineData(".NET 10.0.14", Legacy, Runtime14)]
    [InlineData(".NET 10.0.13", Runtime14, Legacy)]
    [InlineData(".NET 10.0.15", Runtime14, Runtime14)]
    [InlineData(".NET 10.0.130", Legacy, Legacy)]
    [InlineData(".NET 10.0.12", Legacy, Legacy)]
    [InlineData(".NET 10.0", Runtime14, Runtime14)]
    public void Recorded_step_refuses_unknown_patch_or_mixed_qualification_provider_framework(string framework, string qualification, string provider)
    {
        var step = Step(framework, qualification, provider + "/provider-v1");
        Assert.Throws<OutpostEvidenceUnavailableException>(() => OutpostRestoreSchemaMigration.ValidateRecordedStep(step));
    }

    [Theory]
    [InlineData("ef")]
    [InlineData("managed")]
    [InlineData("pcl")]
    [InlineData("native")]
    [InlineData("storage")]
    [InlineData("vfs")]
    public void Runtime14_keeps_exact_provider_backend_and_storage_predicates(string changed)
    {
        var step = Step(".NET 10.0.14", Runtime14, Runtime14 + "/provider-v1");
        var artifact = step.Executor!;
        artifact = changed switch
        {
            "ef" => artifact with { EfProvider = "Microsoft.EntityFrameworkCore, Version=10.0.15.0" },
            "managed" => artifact with { ManagedProvider = "Microsoft.Data.Sqlite, Version=10.0.15.0" },
            "pcl" => artifact with { PclProvider = "SQLitePCLRaw.core, Version=2.1.14.0" },
            "native" => artifact with { Backend = artifact.Backend with { NativeSha256 = new string('0', 64) } },
            "storage" => artifact with { StorageContract = "sqlite-local-v2" },
            "vfs" => artifact with { Backend = artifact.Backend with { Vfs = "unix" } },
            _ => throw new InvalidOperationException()
        };
        var error = Assert.Throws<OutpostEvidenceUnavailableException>(() => OutpostRestoreSchemaMigration.ValidateRecordedStep(step with { Executor = artifact }));
        Assert.Equal("restore-schema-artifact-invalid", error.Message);
    }

    [Fact]
    public void Runtime14_qualification_metadata_is_separate_from_legacy_provenance()
    {
        var assembly = typeof(OutpostDomainSchema).Assembly;
        using var legacyStream = assembly.GetManifestResourceStream("Rimlink.OutpostSchema.qualification.json");
        using var nextStream = assembly.GetManifestResourceStream("Rimlink.OutpostSchema.qualification-runtime14.json");
        Assert.NotNull(legacyStream); Assert.NotNull(nextStream);
        using var legacy = JsonDocument.Parse(legacyStream);
        using var next = JsonDocument.Parse(nextStream);
        Assert.Equal(Legacy, legacy.RootElement.GetProperty("qualificationId").GetString());
        Assert.Equal(".NET 10.0.13", legacy.RootElement.GetProperty("toolchain").GetProperty("runtime").GetString());
        Assert.Equal(Runtime14, next.RootElement.GetProperty("qualificationId").GetString());
        Assert.Equal(Runtime14 + "/provider-v1", next.RootElement.GetProperty("providerProfile").GetString());
        Assert.Equal(".NET 10.0.14", next.RootElement.GetProperty("toolchain").GetProperty("runtime").GetString());
        Assert.Equal("10.0.403", next.RootElement.GetProperty("toolchain").GetProperty("sdk").GetString());
    }

    private static RestoreDomainStep Step(string framework, string qualification, string provider, string vfs = "win32") => new(
        2, OutpostDomainFormat.V1, OutpostDomainFormat.V2, "Additive",
        OutpostDomainSchema.KnownBinding("rimlink-outposts-schema/v1-lf"),
        OutpostDomainSchema.KnownBinding("rimlink-outposts-schema/v2-lf-lf"),
        new(1, qualification, 1, OutpostRestoreSchemaMigration.FromMigration, OutpostDomainSchema.AdminMigrationId,
            Convert.FromHexString("a5ee5d52c1d25f13d4f1d75e74f3cd02763b0667d81f3a74f23d62e7cea8b3f4"), provider),
        new(1, "rimlink-server, Version=0.9.1.0", "18b20563-3958-4718-8587-d7956a4a0702", SHA256.HashData([1]), 1,
            "Rimlink.Server.Storage.Migrations.OutpostAdminCommands", framework,
            "Microsoft.EntityFrameworkCore, Version=10.0.14.0", "Microsoft.Data.Sqlite, Version=10.0.14.0", "SQLitePCLRaw.core, Version=2.1.13.3146",
            new("3.53.3", "2026-06-26 20:14:12 d4c0e51e4aeb96955b99185ab9cde75c339e2c29c3f3f12428d364a10d782c62", vfs,
                vfs == "win32" ? "b7385d722c83fb52142a00477a726723745916d22a555711ee89834c1111fb2e" : "b0f5c48026fedcf9f05cf31c6fadf9e1e001ca59cd85aa68b8b6814e52c18a07",
                "2.1.13.3146"), "sqlite-local-v1"));
}
