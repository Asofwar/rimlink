using System.Security.Cryptography;
using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Backup;
using Rimlink.Server.Tests.Outposts;
using Xunit;

namespace Rimlink.Server.Tests.Backup;

public sealed class OutpostRestoreSchemaMigrationTests
{
    [Fact]
    public async Task Pre_admin_cut_freezes_exact_additive_step_before_any_ddl()
    {
        using var f = await LifecycleFixture.Create(); var h = f.H.F.H.H;
        string archive = await PreAdminCut(f); var procedure = new OutpostRestoreProcedure();
        var request = await OutpostRestoreTests.Request(f, procedure, archive); f.Host.Dispose();
        procedure.Checkpoint = p => { if (p == "AfterMainPrepared") throw new Stop(); };
        await Assert.ThrowsAsync<Stop>(() => procedure.ExecuteAsync(request, default));
        var plan = ReadPlan(f, request.RestoreId); Assert.Equal(2, plan.Version);
        using var json = System.Text.Json.JsonDocument.Parse(OutpostRestoreEvidence.Encode(plan));
        var step = json.RootElement.GetProperty("domainStep");
        Assert.Equal(2, step.GetProperty("version").GetInt32()); Assert.Equal("Additive", step.GetProperty("action").GetString());
        Assert.Equal(1, step.GetProperty("from").GetInt32()); Assert.Equal(2, step.GetProperty("to").GetInt32());
        var migration = step.GetProperty("migration");
        Assert.Equal("20261104093958_OutpostOrdinaryReturns", migration.GetProperty("fromMigration").GetString());
        Assert.Equal("20261105014436_OutpostAdminCommands", migration.GetProperty("toMigration").GetString());
        Assert.Equal("rimlink-outposts-schema-20261105-0520", migration.GetProperty("qualificationId").GetString());
        Assert.Equal(32, migration.GetProperty("recipeSha256").GetBytesFromBase64().Length);
        var executor = step.GetProperty("executor"); Assert.Equal(1, executor.GetProperty("version").GetInt32());
        Assert.Equal(32, executor.GetProperty("artifactSha256").GetBytesFromBase64().Length);
        Assert.True(Guid.TryParseExact(executor.GetProperty("moduleMvid").GetString(), "D", out _));
        Assert.Equal(OutpostBackupTests.Read(archive)[OutpostBackupManifest.FileName], plan.CutManifest);
        var schema = await OutpostDomainSchema.ObserveAsync(h.Db, default); Assert.False(schema.IsCurrent);
        Assert.Equal(plan.Cut.DomainDigest, (await OutpostDomainDigest.ComputeAsync(h.Db, OutpostDomainFormat.V1, default)).ToByteArray());
        Assert.Empty(await h.Db.OutpostTransitions.Where(t => t.Id == request.RestoreId).ToArrayAsync());
        using var held = await h.Anchor.AcquireAsync(default);
        var external = Assert.IsType<KnownAnchorEnvelope>(h.Anchor.ReadEnvelope(held));
        Assert.NotNull(external.Record.Restore);
        // Before any DDL, the same external fence must commit the exact frozen plan, not only its older parent intent.
        Assert.NotEqual(plan.JournalDigest, external.Record.Restore.BackupDigest.ToByteArray());
        byte[] raw = File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(h.Anchor.DatabasePath)!, "restores", request.RestoreId, "main.json"));
        byte[] tag = System.Text.Encoding.UTF8.GetBytes("RLORESTORE-MAIN-BOUND/v1");
        byte[] binding = new byte[tag.Length + 1 + 64];
        Buffer.BlockCopy(tag, 0, binding, 0, tag.Length); // The next byte stays exactly zero.
        Buffer.BlockCopy(plan.JournalDigest, 0, binding, tag.Length + 1, 32);
        Buffer.BlockCopy(SHA256.HashData(raw), 0, binding, tag.Length + 33, 32);
        Assert.Equal(SHA256.HashData(binding), external.Record.Restore.BackupDigest.ToByteArray());
        var prior = AnchorRecordCodec.Decode(plan.Journal.Selection.ExternalRecord!);
        Assert.Equal(prior, external.Record with { Restore = null });
        Assert.Equal(request.RestoreId, external.Record.Restore.Id);
        Assert.Equal(request.TargetBoundary, external.Record.Restore.NewBoundary);
    }

    [Fact]
    public async Task Pre_admin_restore_preserves_C_and_commits_direct_R_v2_after_the_sole_additive_migration()
    {
        using var f = await LifecycleFixture.Create(); var h = f.H.F.H.H;
        string archive = await PreAdminCut(f); byte[] originalArchive = SHA256.HashData(File.ReadAllBytes(archive));
        byte[] prefix = OutpostRestoreEvidence.Encode(await h.Db.OutpostTransitions.AsNoTracking().OrderBy(t => t.Id).ToArrayAsync());
        var procedure = new OutpostRestoreProcedure(); var request = await OutpostRestoreTests.Request(f, procedure, archive); f.Host.Dispose();
        var result = await procedure.ExecuteAsync(request, default); Assert.True(result.Outcome == RestoreOutcome.Completed, result.Reason);
        h.Db.ChangeTracker.Clear(); var root = await h.Db.OutpostTransitions.AsNoTracking().SingleAsync(t => t.Id == request.RestoreId);
        var plan = OutpostRestoreEvidence.Plan(root); Assert.Equal("Additive", plan.DomainStep!.Action);
        Assert.Equal(request.Inspection.Manifest.Authority.HeadId, root.OldHeadId);
        Assert.Equal(prefix, OutpostRestoreEvidence.Encode(await h.Db.OutpostTransitions.AsNoTracking()
            .Where(t => t.Id != root.Id && t.Kind != "RestoreCompletion").OrderBy(t => t.Id).ToArrayAsync()));
        Assert.Equal(request.Inspection.Manifest.DomainDigest, (await ReadRetainedV1(h.Data.Root, plan)).ToByteArray());
        Assert.Equal(OutpostBackupTests.Read(archive)[OutpostBackupManifest.FileName], File.ReadAllBytes(Path.Combine(h.Data.Root, plan.SourceDirectory, OutpostBackupManifest.FileName)));
        Assert.Empty(await h.Db.OutpostAdminCommands.ToArrayAsync());
        var authority = await h.Db.OutpostAuthority.AsNoTracking().SingleAsync();
        var observed = await OutpostAuthority.ReadStoredChainAsync(h.Db, h.Files, authority, new(authority.HeadId, ByteString.CopyFrom(authority.HeadDigest)), true, default);
        Assert.Equal(OutpostDomainFormat.V2, observed.Format);
        Assert.Equal(originalArchive, SHA256.HashData(File.ReadAllBytes(archive)));
        var again = await procedure.ResumeAsync(h.Data.Root, Path.GetDirectoryName(h.Anchor.DatabasePath)!, request.RestoreId, default);
        Assert.Equal(result.HeadId, again.HeadId); Assert.Equal(result.HeadDigest, again.HeadDigest);
    }

    [Theory]
    [InlineData("artifact")]
    [InlineData("mvid")]
    [InlineData("head")]
    [InlineData("noddl-downgrade")]
    public async Task Changed_bound_additive_plan_cannot_use_resume_as_an_unbound_or_legacy_path(string damage)
    {
        using var f = await LifecycleFixture.Create(); var h = f.H.F.H.H;
        string archive = await PreAdminCut(f); var procedure = new OutpostRestoreProcedure();
        var request = await OutpostRestoreTests.Request(f, procedure, archive); f.Host.Dispose();
        procedure.Checkpoint = p => { if (p == "AfterMainPrepared") throw new Stop(); };
        await Assert.ThrowsAsync<Stop>(() => procedure.ExecuteAsync(request, default));
        var plan = ReadPlan(f, request.RestoreId); var step = plan.DomainStep!;
        plan = damage switch
        {
            "artifact" => plan with { DomainStep = step with { Executor = step.Executor! with { ArtifactSha256 = SHA256.HashData([42]) } } },
            "mvid" => plan with { DomainStep = step with { Executor = step.Executor! with { ModuleMvid = Guid.NewGuid().ToString("D") } } },
            "head" => plan with { NextHeadId = Guid.NewGuid().ToString("D") },
            "noddl-downgrade" => plan with { DomainStep = new(1, OutpostDomainFormat.V1, OutpostDomainFormat.V2, "NoDdl", step.Target, step.Target) },
            _ => throw new InvalidOperationException()
        };
        string path = Path.Combine(Path.GetDirectoryName(h.Anchor.DatabasePath)!, "restores", request.RestoreId, "main.json");
        File.WriteAllBytes(path, OutpostRestoreEvidence.Encode(plan));
        var before = await f.Cut(); procedure.Checkpoint = null;
        var result = await procedure.ResumeAsync(h.Data.Root, Path.GetDirectoryName(h.Anchor.DatabasePath)!, request.RestoreId, default);
        Assert.Equal(RestoreOutcome.Maintenance, result.Outcome); Assert.Contains("restore-main-bound-marker-mismatch", result.Reason);
        Assert.Equal(before, await f.Cut());
        Assert.False((await OutpostDomainSchema.ObserveAsync(h.Db, default)).IsCurrent);
        Assert.Empty(await h.Db.OutpostTransitions.Where(t => t.Id == request.RestoreId).ToArrayAsync());
    }

    [Theory]
    [InlineData("AfterMainPlanWritten")]
    [InlineData("AfterMainPlanBound")]
    [InlineData("AfterMainPrepared")]
    [InlineData("BeforeSchemaMigration")]
    [InlineData("AfterSchemaMigration")]
    [InlineData("AfterMainDatabaseCommit")]
    [InlineData("AfterMainCommitted")]
    [InlineData("AfterCompletionCommitted")]
    [InlineData("AfterRestorePublication")]
    public async Task Explicit_additive_resume_preserves_frozen_bytes_across_each_licensed_boundary(string stop)
    {
        using var f = await LifecycleFixture.Create(); var h = f.H.F.H.H;
        string archive = await PreAdminCut(f); var procedure = new OutpostRestoreProcedure();
        var request = await OutpostRestoreTests.Request(f, procedure, archive); f.Host.Dispose();
        procedure.Checkpoint = p => { if (p == stop) throw new Stop(); };
        await Assert.ThrowsAsync<Stop>(() => procedure.ExecuteAsync(request, default));
        string anchor = Path.GetDirectoryName(h.Anchor.DatabasePath)!;
        string path = Path.Combine(anchor, "restores", request.RestoreId, "main.json"); byte[] frozen = File.ReadAllBytes(path);
        var result = await new OutpostRestoreProcedure().ResumeAsync(h.Data.Root, anchor, request.RestoreId, default);
        Assert.True(result.Outcome == RestoreOutcome.Completed, result.Reason); Assert.Equal(frozen, File.ReadAllBytes(path));
        h.Db.ChangeTracker.Clear(); Assert.Single(await h.Db.OutpostTransitions.Where(t => t.Kind == "BulkRestore").ToArrayAsync());
        var authority = await h.Db.OutpostAuthority.AsNoTracking().SingleAsync();
        var domain = await OutpostAuthority.ReadStoredChainAsync(h.Db, h.Files, authority, new(authority.HeadId, ByteString.CopyFrom(authority.HeadDigest)), true, default);
        Assert.Equal(OutpostDomainFormat.V2, domain.Format);
        var replay = await new OutpostRestoreProcedure().ResumeAsync(h.Data.Root, anchor, request.RestoreId, default);
        Assert.Equal(result.HeadId, replay.HeadId); Assert.Equal(result.HeadDigest, replay.HeadDigest);
    }

    [Theory]
    [InlineData("AfterMainPlanWritten")]
    [InlineData("AfterMainPlanBound")]
    [InlineData("AfterMainPrepared")]
    [InlineData("AfterSchemaMigration")]
    [InlineData("AfterMainDatabaseCommit")]
    [InlineData("AfterMainCommitted")]
    [InlineData("AfterCompletionCommitted")]
    public async Task Confirmed_additive_backout_preserves_binding_and_restores_exact_previous_E(string stop)
    {
        using var f = await LifecycleFixture.Create(); var h = f.H.F.H.H;
        string archive = await PreAdminCut(f); var procedure = new OutpostRestoreProcedure();
        var request = await OutpostRestoreTests.Request(f, procedure, archive); f.Host.Dispose();
        procedure.Checkpoint = p => { if (p == stop) throw new Stop(); };
        await Assert.ThrowsAsync<Stop>(() => procedure.ExecuteAsync(request, default));
        string anchor = Path.GetDirectoryName(h.Anchor.DatabasePath)!;
        var selected = procedure.InspectBackout(h.Data.Root, anchor, request.RestoreId);
        selected = selected with { ConfirmationDigest = OutpostRestoreJournalCodec.BackoutDigest(selected) };
        var result = await new OutpostRestoreProcedure().BackoutAsync(selected, default);
        Assert.True(result.Outcome == RestoreOutcome.BackedOut, result.Reason);
        Assert.Equal(request.Inspection.Destination.External!.Committed.Id, result.HeadId);
        h.Db.ChangeTracker.Clear(); Assert.Empty(await h.Db.OutpostTransitions.Where(t => t.Id == request.RestoreId).ToArrayAsync());
        using (var held = await h.Anchor.AcquireAsync(default)) Assert.Equal(request.Inspection.Destination.External, h.Anchor.Read(held));
        var again = await new OutpostRestoreProcedure().BackoutAsync(selected, default); Assert.Equal(result.HeadId, again.HeadId);
    }

    internal static RestoreMainPlan ReadPlan(LifecycleFixture f, string id) => OutpostRestoreEvidence.DecodePlan(File.ReadAllBytes(
        Path.Combine(Path.GetDirectoryName(f.H.F.H.H.Anchor.DatabasePath)!, "restores", id, "main.json")));

    internal static async Task<string> PreAdminCut(LifecycleFixture f)
    {
        var h = f.H.F.H.H; string archive = await OutpostBackupTests.Create(f); var entries = OutpostBackupTests.Read(archive);
        var original = OutpostBackupManifest.Decode(entries[OutpostBackupManifest.FileName]);
        string work = Path.Combine(h.Root, "pre-admin-cut-work"); Directory.CreateDirectory(work);
        File.WriteAllBytes(Path.Combine(work, BackupArchive.DatabaseFile), entries[BackupArchive.DatabaseFile]);
        string snapshot = Path.Combine(h.Root, "pre-admin-cut.db");
        await using (var db = OutpostRestoreProcedure.OpenRestoreDatabase(work))
        {
            await db.GetService<IMigrator>().MigrateAsync("20261104093958_OutpostOrdinaryReturns");
            Assert.False((await OutpostDomainSchema.ObserveAsync(db, default)).IsCurrent);
            Assert.Equal(original.DomainDigest, (await OutpostDomainDigest.ComputeAsync(db, OutpostDomainFormat.V1, default)).ToByteArray());
            await db.Database.OpenConnectionAsync(); using var cut = db.Database.GetDbConnection().CreateCommand();
            cut.CommandText = "VACUUM INTO $path"; var parameter = cut.CreateParameter(); parameter.ParameterName = "$path"; parameter.Value = snapshot;
            cut.Parameters.Add(parameter); await cut.ExecuteNonQueryAsync();
        }
        byte[] bytes = File.ReadAllBytes(snapshot); entries[BackupArchive.DatabaseFile] = bytes;
        var inventory = original.Files.Select(item => item.Path == BackupArchive.DatabaseFile
            ? item with { Length = bytes.LongLength, Sha256 = SHA256.HashData(bytes) } : item).ToArray();
        // Genuine pre-admin schema, existing real v1 chain/snapshots and closed legacy envelope; no simulated RPC/evidence.
        entries[OutpostBackupManifest.FileName] = new OutpostBackupManifest(1, original.Authority, original.CutUtc,
            original.DomainDigest, inventory, original.Proofs).Encode();
        return OutpostBackupFormatInspectionTests.Write(h.Root, entries);
    }

    private static async Task<ByteString> ReadRetainedV1(string root, RestoreMainPlan plan)
    {
        await using var db = OutpostRestoreProcedure.OpenRestoreDatabase(Path.Combine(root, plan.SourceDirectory), readOnly: true);
        Assert.False((await OutpostDomainSchema.ObserveAsync(db, default)).IsCurrent);
        return await OutpostDomainDigest.ComputeAsync(db, OutpostDomainFormat.V1, default);
    }
    private sealed class Stop : Exception;
}
