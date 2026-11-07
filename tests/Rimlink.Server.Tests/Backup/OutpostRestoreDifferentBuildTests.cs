using System.Security.Cryptography;
using System.Text.Json;
using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Application.Saves;
using Rimlink.Server.Backup;
using Rimlink.Server.Storage;
using Rimlink.Server.Tests.Outposts;
using Xunit;
using Xunit.Abstractions;

namespace Rimlink.Server.Tests.Backup;

/// <summary>Explicit two-build local fixture hand-off; no-op execution alone is not qualification.</summary>
public sealed class OutpostRestoreDifferentBuildTests(ITestOutputHelper output)
{
    private sealed record Input(string Data, string Anchor, string Id, byte[] PlanDigest, bool PostSchema);
    [Fact]
    public async Task Owned_additive_two_build_worker()
    {
        string? export = Environment.GetEnvironmentVariable("RIMLINK_TEST_ADDITIVE_BUILD_EXPORT");
        string? import = Environment.GetEnvironmentVariable("RIMLINK_TEST_ADDITIVE_BUILD_IMPORT");
        if (export is null && import is null) return;
        Assert.NotEqual(export is not null, import is not null);
        string directory = (export ?? import)!; Assert.True(Path.IsPathFullyQualified(directory)); Assert.True(Directory.Exists(directory));
        foreach (bool post in new[] { false, true })
        {
            string inputPath = Path.Combine(directory, post ? "post.json" : "pre.json");
            if (export is not null)
            {
                using var f = await LifecycleFixture.Create(); var h = f.H.F.H.H;
                string archive = await OutpostRestoreSchemaMigrationTests.PreAdminCut(f);
                var procedure = new OutpostRestoreProcedure(); var request = await OutpostRestoreTests.Request(f, procedure, archive); f.Host.Dispose();
                procedure.Checkpoint = p => { if (p == (post ? "AfterSchemaMigration" : "AfterMainPrepared")) throw new Stop(); };
                await Assert.ThrowsAsync<Stop>(() => procedure.ExecuteAsync(request, default));
                string anchor = Path.GetDirectoryName(h.Anchor.DatabasePath)!;
                byte[] plan = File.ReadAllBytes(Path.Combine(anchor, "restores", request.RestoreId, "main.json"));
                using var file = new FileStream(inputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                file.Write(JsonSerializer.SerializeToUtf8Bytes(new Input(h.Data.Root, anchor, request.RestoreId, SHA256.HashData(plan), post), OutpostBackupManifest.Json));
                file.Flush(true); h.KeepForDiagnostics = true; output.WriteLine("TWO-BUILD-EXPORTED:" + (post ? "post" : "pre"));
            }
            else await Import(inputPath, post);
        }
    }

    private async Task Import(string inputPath, bool post)
    {
        var selected = JsonSerializer.Deserialize<Input>(File.ReadAllBytes(inputPath), OutpostBackupManifest.Json)!; Assert.Equal(post, selected.PostSchema);
        string path = Path.Combine(selected.Anchor, "restores", selected.Id, "main.json"); byte[] original = File.ReadAllBytes(path);
        Assert.Equal(selected.PlanDigest, SHA256.HashData(original)); var plan = OutpostRestoreEvidence.DecodePlan(original);
        string framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription;
        if (Environment.GetEnvironmentVariable("RIMLINK_TEST_ADDITIVE_BUILD_SOURCE_RUNTIME") is { } expectedSourceRuntime)
        {
            Assert.Equal(expectedSourceRuntime, plan.DomainStep!.Executor!.Framework);
            Assert.NotEqual(expectedSourceRuntime, framework);
        }
        byte[] actualArtifact = SHA256.HashData(File.ReadAllBytes(typeof(OutpostRestoreProcedure).Assembly.Location));
        Assert.NotEqual(plan.DomainStep!.Executor!.ArtifactSha256, actualArtifact);
        Assert.NotEqual(plan.DomainStep.Executor.ModuleMvid, typeof(OutpostRestoreProcedure).Assembly.ManifestModule.ModuleVersionId.ToString("D"));
        var beforeFiles = Directory.EnumerateFiles(selected.Data, "*", SearchOption.AllDirectories)
            .Where(p => !p.EndsWith("-shm") && !p.EndsWith("-wal")).Order().Select(p => (p, Bytes: File.ReadAllBytes(p))).ToArray();
        if (post) OutpostRestoreSchemaMigration.ExecutionProbe.Value = operation => throw new InvalidOperationException("post-schema must not inspect the new build executor: " + operation);
        try
        {
            var result = await new OutpostRestoreProcedure().ResumeAsync(selected.Data, selected.Anchor, selected.Id, default);
            Assert.Equal(post ? RestoreOutcome.Completed : RestoreOutcome.Maintenance, result.Outcome);
            Assert.Equal(original, File.ReadAllBytes(path));
            await using var db = OutpostRestoreProcedure.OpenRestoreDatabase(selected.Data);
            var schema = await OutpostDomainSchema.ObserveAsync(db, default); Assert.Equal(post, schema.IsCurrent);
            if (!post)
            {
                Assert.Contains("restore-schema-frozen-executor-mismatch", result.Reason);
                Assert.Empty(await db.OutpostTransitions.Where(t => t.Id == selected.Id).ToArrayAsync());
                var row = await db.OutpostAuthority.AsNoTracking().SingleAsync();
                var unchanged = await Rimlink.Server.Application.Outposts.OutpostAuthority.ReadStoredChainAsync(db, new SaveFiles(DataDirectory.Existing(selected.Data)),
                    row, new(row.HeadId, ByteString.CopyFrom(row.HeadDigest)), false, default);
                Assert.Equal(OutpostDomainFormat.V1, unchanged.Format); Assert.Equal(plan.Cut.DomainDigest, unchanged.Digest.ToByteArray());
                foreach (var file in beforeFiles)
                {
                    byte[] expected = file.Bytes.ToArray();
                    if (file.p == Path.Combine(selected.Data, BackupArchive.DatabaseFile) && expected[18] == 1 && expected[19] == 1)
                    {
                        // Explicit restore installs the qualified WAL mode before this refusal. Native SQLite changes
                        // ONLY read/write versions and the coupled change-counter/version-valid-for in the working MAIN.
                        // The immutable retained C and every other byte/file remain exact; no schema/row change is allowed.
                        expected[18] = expected[19] = 2;
                        uint change = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(expected.AsSpan(24, 4));
                        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(expected.AsSpan(24, 4), checked(change + 1));
                        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(expected.AsSpan(92, 4), checked(change + 1));
                    }
                    Assert.Equal(expected, File.ReadAllBytes(file.p));
                }
            }
            else
            {
                var row = await db.OutpostAuthority.AsNoTracking().SingleAsync();
                var observed = await Rimlink.Server.Application.Outposts.OutpostAuthority.ReadStoredChainAsync(db, new SaveFiles(DataDirectory.Existing(selected.Data)),
                    row, new(row.HeadId, ByteString.CopyFrom(row.HeadDigest)), true, default);
                Assert.Equal(OutpostDomainFormat.V2, observed.Format);
                var root = await db.OutpostTransitions.AsNoTracking().SingleAsync(t => t.Id == selected.Id);
                Assert.Equal(original, root.Intent);
                await new OutpostSnapshotGuard(db, new SaveFiles(DataDirectory.Existing(selected.Data))).ValidateStoredEvidenceCoreAsync(default);
            }
            output.WriteLine("TWO-BUILD-QUALIFIED:" + (post ? "post:no-execution" : "pre:refused")
                + ":old=" + Convert.ToHexStringLower(plan.DomainStep.Executor.ArtifactSha256) + ":new=" + Convert.ToHexStringLower(actualArtifact)
                + ":source-runtime=" + plan.DomainStep.Executor.Framework + ":runtime=" + framework
                + ":qualification=" + plan.DomainStep.Migration!.QualificationId + ":provider=" + plan.DomainStep.Migration.ProviderProfile);
        }
        finally { OutpostRestoreSchemaMigration.ExecutionProbe.Value = null; }
    }
    private sealed class Stop : Exception;
}
