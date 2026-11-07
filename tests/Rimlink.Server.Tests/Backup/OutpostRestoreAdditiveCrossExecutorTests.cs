using System.Security.Cryptography;
using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Application.Saves;
using Rimlink.Server.Backup;
using Rimlink.Server.Hosting;
using Rimlink.Server.Storage;
using Rimlink.Server.Tests.Outposts;
using Rimlink.Server.Tests.Support;
using Xunit;
using Xunit.Abstractions;

namespace Rimlink.Server.Tests.Backup;

/// <summary>Explicit owned fixture transport; an ordinary no-op run is not cross-executor qualification.</summary>
public sealed class OutpostRestoreAdditiveCrossExecutorTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Owned_additive_restore_cross_executor_worker()
    {
        string? export = Environment.GetEnvironmentVariable("RIMLINK_TEST_ADDITIVE_EXPORT");
        string? import = Environment.GetEnvironmentVariable("RIMLINK_TEST_ADDITIVE_IMPORT");
        if (export is null && import is null) return;
        Assert.NotEqual(export is not null, import is not null);
        string directory = (export ?? import)!; Assert.True(Path.IsPathFullyQualified(directory)); Assert.True(Directory.Exists(directory));
        string archive = Path.Combine(directory, "pre-admin.tar.zst");
        if (export is not null)
        {
            using var f = await LifecycleFixture.Create();
            File.Copy(await OutpostRestoreSchemaMigrationTests.PreAdminCut(f), archive, false);
            output.WriteLine("ADDITIVE-EXPORTED:" + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(archive))));
            return;
        }
        byte[] archiveDigest = SHA256.HashData(File.ReadAllBytes(archive));
        var entries = OutpostBackupTests.Read(archive); var manifest = OutpostBackupManifest.Decode(entries[OutpostBackupManifest.FileName]);
        Assert.Equal(1, manifest.Version); Assert.Null(manifest.Domain);
        string root = Path.Combine(Path.GetTempPath(), "rimlink-additive-cross-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            string data = Path.Combine(root, "main"), external = Path.Combine(root, "external"), original = Path.Combine(root, "source");
            Directory.CreateDirectory(data); Directory.CreateDirectory(external); Directory.CreateDirectory(original);
            File.WriteAllBytes(Path.Combine(original, BackupArchive.DatabaseFile), entries[BackupArchive.DatabaseFile]);
            byte[] prefix;
            await using (var source = OutpostRestoreProcedure.OpenRestoreDatabase(original, readOnly: true))
                prefix = OutpostRestoreEvidence.Encode(await source.OutpostTransitions.AsNoTracking().OrderBy(t => t.Id).ToArrayAsync());
            var anchor = new OutpostAnchorStore(data, Options.Create(new OutpostOptions
            { AnchorDirectory = external, StorageContract = "sqlite-local-v1", QualifiedLinuxFileSystem = LocalTestStorage.LinuxQualification(data) }));
            anchor.Provision(); var procedure = new OutpostRestoreProcedure();
            var request = await OutpostRestoreModeTests.Request(procedure, archive, data, external);
            var result = await procedure.ExecuteAsync(request, default); Assert.True(result.Outcome == RestoreOutcome.Completed, result.Reason);
            await using var db = OutpostRestoreProcedure.OpenRestoreDatabase(data);
            var authority = await db.OutpostAuthority.AsNoTracking().SingleAsync();
            var domain = await Rimlink.Server.Application.Outposts.OutpostAuthority.ReadStoredChainAsync(db, new SaveFiles(DataDirectory.Existing(data)),
                authority, new(authority.HeadId, ByteString.CopyFrom(authority.HeadDigest)), true, default);
            var transition = await db.OutpostTransitions.AsNoTracking().SingleAsync(t => t.Id == request.RestoreId);
            var plan = OutpostRestoreEvidence.Plan(transition); var step = plan.DomainStep!;
            Assert.Equal("Additive", step.Action); Assert.Equal(2, step.Version); Assert.Equal(OutpostDomainFormat.V2, domain.Format);
            Assert.Equal(step.Target.ProfileId, domain.Schema.ProfileId); Assert.Equal(step.Target.Digest, domain.Schema.Digest.ToByteArray());
            Assert.Equal(entries[OutpostBackupManifest.FileName], plan.CutManifest);
            Assert.Equal(prefix, OutpostRestoreEvidence.Encode(await db.OutpostTransitions.AsNoTracking()
                .Where(t => t.Id != request.RestoreId && t.Kind != "RestoreCompletion").OrderBy(t => t.Id).ToArrayAsync()));
            foreach (var file in manifest.Files) Assert.Equal(file.Sha256, SHA256.HashData(File.ReadAllBytes(Path.Combine(data, plan.SourceDirectory, file.Path))));
            Assert.Empty(await db.OutpostAdminCommands.ToArrayAsync());
            await new OutpostSnapshotGuard(db, new SaveFiles(DataDirectory.Existing(data))).ValidateStoredEvidenceCoreAsync(default);
            var repeated = await procedure.ResumeAsync(data, external, request.RestoreId, default);
            Assert.Equal(result.HeadId, repeated.HeadId); Assert.Equal(result.HeadDigest, repeated.HeadDigest);
            Assert.Equal(archiveDigest, SHA256.HashData(File.ReadAllBytes(archive)));
            output.WriteLine("ADDITIVE-QUALIFIED:" + step.Source.ProfileId + "=>" + step.Target.ProfileId
                + ":recipe=" + Convert.ToHexStringLower(step.Migration!.RecipeSha256) + ":cut=" + Convert.ToHexStringLower(archiveDigest));
        }
        finally { Directory.Delete(root, true); }
    }
}
