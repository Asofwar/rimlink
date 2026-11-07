using System.Data;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Rimlink.Server.Storage;
using Rimlink.Server.Storage.Migrations;

namespace Rimlink.Server.Application.Outposts;

internal sealed record RestoreQualifiedMigration(int Version, string QualificationId, int QualificationVersion,
    string FromMigration, string ToMigration, byte[] RecipeSha256, string ProviderProfile);
internal sealed record RestoreExecutorArtifact(int Version, string AssemblyIdentity, string ModuleMvid, byte[] ArtifactSha256,
    long ArtifactLength, string MigrationType, string Framework, string EfProvider, string ManagedProvider,
    string PclProvider, OutpostSqliteIdentity Backend, string StorageContract);

/// <summary>Frozen, closed schema edges. Historical validation never resolves an executor or generates SQL.</summary>
internal static class OutpostRestoreSchemaMigration
{
    // Test-only asynchronous-flow observer: it can refuse work, never authorize or select a schema edge.
    internal static readonly AsyncLocal<Action<string>?> ExecutionProbe = new();
    internal const string FromMigration = "20261104093958_OutpostOrdinaryReturns";
    private const string QualificationId = "rimlink-outposts-schema-20261105-0520";
    private const string ProviderProfile = "rimlink-outposts-schema-20261105-0520/provider-v1";
    private const string CrLfRecipe = "64358bbb34d8080afaaeeb745041b9fdeccadc28af8ce84666734ce3b8324525";
    private const string LfRecipe = "a5ee5d52c1d25f13d4f1d75e74f3cd02763b0667d81f3a74f23d62e7cea8b3f4";
    private static readonly (string Source, string Recipe, string Target)[] Edges =
    [
        ("v1-crlf", CrLfRecipe, "v2-crlf-crlf"), ("v1-lf", CrLfRecipe, "v2-lf-crlf"),
        ("v1-crlf", LfRecipe, "v2-crlf-lf"), ("v1-lf", LfRecipe, "v2-lf-lf")
    ];

    internal static async Task<RestoreDomainStep> SelectAsync(RimlinkDbContext source,
        OutpostAuthority.StoredDomainObservation observed, CancellationToken ct)
    {
        if (observed.Format != OutpostDomainFormat.V1 || observed.Schema.IsCurrent)
            throw Invalid("restore-schema-selection-invalid");
        var artifact = await ObserveExecutorAsync(source, ct);
        byte[] recipe = Recipe(source);
        var edge = Edges.SingleOrDefault(e => "rimlink-outposts-schema/" + e.Source == observed.Schema.ProfileId
            && e.Recipe == Convert.ToHexStringLower(recipe));
        if (edge.Target is null) throw Invalid("restore-schema-recipe-unqualified");
        var step = new RestoreDomainStep(2, OutpostDomainFormat.V1, OutpostDomainFormat.V2, "Additive",
            new(observed.Schema.ProfileId, observed.Schema.Digest.ToByteArray()), OutpostDomainSchema.KnownBinding("rimlink-outposts-schema/" + edge.Target),
            new(1, QualificationId, 1, FromMigration, OutpostDomainSchema.AdminMigrationId, recipe, ProviderProfile), artifact);
        ValidateRecordedStep(step);
        return step;
    }

    internal static void ValidateRecordedStep(RestoreDomainStep step)
    {
        var migration = step.Migration; var artifact = step.Executor;
        if (step.Version != 2 || step.Action != "Additive" || step.From != OutpostDomainFormat.V1 || step.To != OutpostDomainFormat.V2
            || migration is null || migration.Version != 1 || migration.QualificationVersion != 1 || migration.QualificationId != QualificationId
            || migration.ProviderProfile != ProviderProfile || migration.FromMigration != FromMigration || migration.ToMigration != OutpostDomainSchema.AdminMigrationId
            || migration.RecipeSha256 is not { Length: 32 } || !OutpostDomainSchema.IsKnownBinding(step.Source, false)
            || !OutpostDomainSchema.IsKnownBinding(step.Target, true) || !Edges.Any(e => "rimlink-outposts-schema/" + e.Source == step.Source.ProfileId
                && "rimlink-outposts-schema/" + e.Target == step.Target.ProfileId && e.Recipe == Convert.ToHexStringLower(migration.RecipeSha256)))
            throw Invalid("restore-schema-recorded-step-invalid");
        ValidateArtifact(artifact);
    }

    internal static async Task RequireFrozenExecutorForDdlAsync(RimlinkDbContext db, RestoreDomainStep step, CancellationToken ct)
    {
        ValidateRecordedStep(step);
        var schema = await OutpostDomainSchema.ObserveAsync(db, ct);
        if (schema.ProfileId != step.Source.ProfileId || !schema.Digest.Span.SequenceEqual(step.Source.Digest))
            throw Invalid("restore-schema-executor-precondition-mismatch");
        var actual = await ObserveExecutorAsync(db, ct);
        if (!OutpostRestoreEvidence.Same(actual, step.Executor)) throw Invalid("restore-schema-frozen-executor-mismatch");
        if (!Recipe(db).AsSpan().SequenceEqual(step.Migration!.RecipeSha256)) throw Invalid("restore-schema-frozen-recipe-mismatch");
    }

    internal static async Task ApplyFrozenAsync(RimlinkDbContext db, RestoreDomainStep step, CancellationToken ct)
    {
        ValidateRecordedStep(step);
        ExecutionProbe.Value?.Invoke("migrator");
        await db.GetService<IMigrator>().MigrateAsync(step.Migration!.ToMigration, ct);
    }

    internal static byte[] Recipe(RimlinkDbContext db)
    {
        // Explicit bounds: a later migration can never expand this attempt to 'latest'. No text normalization.
        ExecutionProbe.Value?.Invoke("recipe");
        string script = db.GetService<IMigrator>().GenerateScript(FromMigration, OutpostDomainSchema.AdminMigrationId);
        if (Encoding.UTF8.GetByteCount(script) is <= 0 or > 1024 * 1024) throw Invalid("restore-schema-recipe-bounds");
        return SHA256.HashData(Encoding.UTF8.GetBytes(script));
    }

    internal static async Task<RestoreExecutorArtifact> ObserveExecutorAsync(RimlinkDbContext db, CancellationToken ct)
    {
        ExecutionProbe.Value?.Invoke("artifact");
        var assembly = typeof(OutpostRestoreSchemaMigration).Assembly;
        if (typeof(OutpostAdminCommands).Assembly != assembly || assembly.IsDynamic || string.IsNullOrEmpty(assembly.Location))
            throw Invalid("restore-schema-artifact-unavailable");
        var (digest, length) = ObserveLoadedArtifact(assembly);
        var connection = (SqliteConnection)db.Database.GetDbConnection(); bool open = connection.State == ConnectionState.Closed;
        try
        {
            if (open) await connection.OpenAsync(ct);
            OutpostSqliteIdentity backend;
            if (SQLitePCL.raw.sqlite3_db_readonly(connection.Handle!, "main") == 1)
                backend = OutpostSqliteBackend.ValidateReadOnlyIdentity(connection);
            else
            {
                var actual = OutpostSqliteBackend.Validate(connection, OutpostDatabaseRole.Domain);
                backend = new(actual.NativeVersion, actual.SourceId, actual.Vfs, actual.NativeSha256, actual.ProviderVersion);
            }
            var result = new RestoreExecutorArtifact(1, assembly.FullName!, assembly.ManifestModule.ModuleVersionId.ToString("D"), digest, length,
                typeof(OutpostAdminCommands).FullName!, RuntimeInformation.FrameworkDescription,
                typeof(DbContext).Assembly.FullName!, typeof(SqliteConnection).Assembly.FullName!, typeof(SQLitePCL.raw).Assembly.FullName!, backend, "sqlite-local-v1");
            ValidateArtifact(result);
            return result;
        }
        finally { if (open) await connection.CloseAsync(); }
    }

    private static unsafe (byte[] Digest, long Length) ObserveLoadedArtifact(Assembly assembly)
    {
        string path = Path.GetFullPath(assembly.Location);
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw Invalid("restore-schema-artifact-link");
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length is <= 0 or > 64 * 1024 * 1024) throw Invalid("restore-schema-artifact-bounds");
        using (var pe = new PEReader(file, PEStreamOptions.LeaveOpen))
        {
            if (!pe.HasMetadata || !assembly.TryGetRawMetadata(out byte* loaded, out int count)
                || !new ReadOnlySpan<byte>(loaded, count).SequenceEqual(pe.GetMetadata().GetContent().AsSpan()))
                throw Invalid("restore-schema-loaded-artifact-mismatch");
            var metadata = pe.GetMetadataReader();
            if (metadata.GetGuid(metadata.GetModuleDefinition().Mvid) != assembly.ManifestModule.ModuleVersionId
                || metadata.GetAssemblyDefinition().GetAssemblyName().FullName != assembly.FullName)
                throw Invalid("restore-schema-loaded-artifact-mismatch");
            foreach (string name in new[] { "Up", "Down" })
            {
                var method = typeof(OutpostAdminCommands).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                    ?? throw Invalid("restore-schema-migration-method-unavailable");
                var definition = metadata.GetMethodDefinition((MethodDefinitionHandle)MetadataTokens.Handle(method.MetadataToken));
                if (!method.GetMethodBody()!.GetILAsByteArray()!.AsSpan().SequenceEqual(pe.GetMethodBody(definition.RelativeVirtualAddress).GetILContent().AsSpan()))
                    throw Invalid("restore-schema-loaded-migration-mismatch");
            }
        }
        // As for the native backend, this binds the loaded artifact in a trusted immutable installation;
        // it is not a general attestation against arbitrary mapped-memory tampering or a plugin loader.
        file.Position = 0; return (SHA256.HashData(file), file.Length);
    }

    private static void ValidateArtifact(RestoreExecutorArtifact? artifact)
    {
        if (artifact is null || artifact.Version != 1 || artifact.ArtifactSha256 is not { Length: 32 }
            || artifact.ArtifactLength is <= 0 or > 64 * 1024 * 1024 || !Guid.TryParseExact(artifact.ModuleMvid, "D", out var mvid) || mvid == Guid.Empty
            || !Identity(artifact.AssemblyIdentity, "rimlink-server", null) || artifact.MigrationType != "Rimlink.Server.Storage.Migrations.OutpostAdminCommands"
            || artifact.Framework != ".NET 10.0.13" || !Identity(artifact.EfProvider, "Microsoft.EntityFrameworkCore", "10.0.14.0")
            || !Identity(artifact.ManagedProvider, "Microsoft.Data.Sqlite", "10.0.14.0") || !Identity(artifact.PclProvider, "SQLitePCLRaw.core", "2.1.13.3146")
            || artifact.StorageContract != "sqlite-local-v1" || artifact.Backend is not { } backend || backend.NativeVersion != "3.53.3"
            || backend.SourceId != "2026-06-26 20:14:12 d4c0e51e4aeb96955b99185ab9cde75c339e2c29c3f3f12428d364a10d782c62"
            || backend.ProviderVersion != "2.1.13.3146" || !(backend.Vfs == "win32" && backend.NativeSha256 == "b7385d722c83fb52142a00477a726723745916d22a555711ee89834c1111fb2e"
                || backend.Vfs == "unix" && backend.NativeSha256 == "b0f5c48026fedcf9f05cf31c6fadf9e1e001ca59cd85aa68b8b6814e52c18a07"))
            throw Invalid("restore-schema-artifact-invalid");
    }

    private static bool Identity(string value, string name, string? version)
    {
        if (value is null || value.Length is 0 or > 1024 || value.Any(char.IsControl)) return false;
        try { var identity = new AssemblyName(value); return identity.Name == name && (version is null || identity.Version?.ToString() == version); }
        catch (Exception error) when (error is ArgumentException or FileLoadException) { return false; }
    }
    private static OutpostEvidenceUnavailableException Invalid(string reason) => new(reason);
}
