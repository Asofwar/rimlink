using System.Security.Cryptography;
using System.Text;
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

/// <summary>Explicit owned fixture transport; normal no-op execution is not cross-executor qualification.</summary>
public sealed class OutpostRestoreCrossExecutorTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Owned_restore_cross_executor_worker()
    {
        string? export = Environment.GetEnvironmentVariable("RIMLINK_TEST_RESTORE_EXPORT");
        string? import = Environment.GetEnvironmentVariable("RIMLINK_TEST_RESTORE_IMPORT");
        if (export is null && import is null) return;
        Assert.NotEqual(export is not null, import is not null);
        string selected = (export ?? import)!;
        Assert.True(Path.IsPathFullyQualified(selected)); Assert.True(Directory.Exists(selected));
        foreach (bool sourceV2 in new[] { false, true })
        {
            string path = Path.Combine(selected, sourceV2 ? "domain-v2.tar.zst" : "domain-v1.tar.zst");
            if (export is not null)
            {
                using var f = await LifecycleFixture.Create();
                if (sourceV2)
                {
                    var command = await OutpostAdminActivationTests.CommandForCurrent(f.H.F.H.H);
                    await OutpostAdminActivationTests.Service(f.H.F.H.H).ActivateDomainFormatAsync(command.Intent.Actor, command, default);
                }
                string archive = await OutpostBackupTests.Create(f);
                var entries = OutpostBackupTests.Read(archive);
                entries[OutpostBackupManifest.FileName] = Encoding.UTF8.GetBytes(" \n" + Encoding.UTF8.GetString(entries[OutpostBackupManifest.FileName]) + "\n");
                File.Copy(OutpostBackupFormatInspectionTests.Write(f.H.F.H.H.Root, entries), path, false);
                output.WriteLine("RESTORE-EXPORTED:" + Path.GetFileName(path) + ":" + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))));
            }
            else await Import(path, sourceV2);
        }
    }

    private async Task Import(string archive, bool sourceV2)
    {
        byte[] archiveDigest = SHA256.HashData(File.ReadAllBytes(archive));
        var entries = OutpostBackupTests.Read(archive); byte[] manifestBytes = entries[OutpostBackupManifest.FileName];
        var manifest = OutpostBackupManifest.Decode(manifestBytes); Assert.NotNull(manifest.Domain);
        Assert.Equal(sourceV2 ? OutpostDomainFormat.V2 : OutpostDomainFormat.V1, manifest.Domain.Format);
        string root = Path.Combine(Path.GetTempPath(), "rimlink-restore-cross-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string data = Path.Combine(root, "main"), external = Path.Combine(root, "external"), source = Path.Combine(root, "source");
            Directory.CreateDirectory(data); Directory.CreateDirectory(external); Directory.CreateDirectory(source);
            File.WriteAllBytes(Path.Combine(source, BackupArchive.DatabaseFile), entries[BackupArchive.DatabaseFile]);
            byte[] prefix;
            await using (var original = OutpostRestoreProcedure.OpenRestoreDatabase(source, readOnly: true))
                prefix = OutpostRestoreEvidence.Encode(await original.OutpostTransitions.AsNoTracking().OrderBy(t => t.Id).ToArrayAsync());
            var store = new OutpostAnchorStore(data, Options.Create(new OutpostOptions
            { AnchorDirectory = external, StorageContract = "sqlite-local-v1", QualifiedLinuxFileSystem = LocalTestStorage.LinuxQualification(data) }));
            store.Provision(); var procedure = new OutpostRestoreProcedure();
            var request = await OutpostRestoreModeTests.Request(procedure, archive, data, external);
            var result = await procedure.ExecuteAsync(request, default);
            Assert.True(result.Outcome == RestoreOutcome.Completed, result.Reason);
            await using var db = OutpostRestoreProcedure.OpenRestoreDatabase(data);
            var authority = await db.OutpostAuthority.AsNoTracking().SingleAsync();
            var observed = await Rimlink.Server.Application.Outposts.OutpostAuthority.ReadStoredChainAsync(db, new SaveFiles(DataDirectory.Existing(data)),
                authority, new(authority.HeadId, ByteString.CopyFrom(authority.HeadDigest)), true, default);
            Assert.Equal(OutpostDomainFormat.V2, observed.Format);
            Assert.Equal(manifest.Domain.Schema.ProfileId, observed.Schema.ProfileId);
            Assert.Equal(manifest.Domain.Schema.Digest, observed.Schema.Digest.ToByteArray());
            var rootTransition = await db.OutpostTransitions.AsNoTracking().SingleAsync(t => t.Id == request.RestoreId);
            var plan = OutpostRestoreEvidence.Plan(rootTransition); Assert.Equal(2, plan.Version);
            Assert.Equal("NoDdl", plan.DomainStep!.Action); Assert.Equal(manifestBytes, plan.CutManifest);
            Assert.True(OutpostRestoreEvidence.Same(plan.DomainStep.Source, plan.DomainStep.Target));
            Assert.Equal(plan.DomainStep.Source.Digest, plan.DomainStep.Target.Digest);
            Assert.Equal(prefix, OutpostRestoreEvidence.Encode(await db.OutpostTransitions.AsNoTracking()
                .Where(t => t.Id != request.RestoreId && t.Kind != "RestoreCompletion").OrderBy(t => t.Id).ToArrayAsync()));
            Assert.Equal(manifestBytes, File.ReadAllBytes(Path.Combine(data, plan.SourceDirectory, OutpostBackupManifest.FileName)));
            foreach (var file in manifest.Files)
                Assert.Equal(file.Sha256, SHA256.HashData(File.ReadAllBytes(Path.Combine(data, plan.SourceDirectory, file.Path))));
            Assert.Equal(sourceV2 ? 1 : 0, await db.OutpostAdminCommands.CountAsync());
            var again = await procedure.ResumeAsync(data, external, request.RestoreId, default);
            Assert.Equal(result.HeadId, again.HeadId); Assert.Equal(result.HeadDigest, again.HeadDigest);
            Assert.Equal(archiveDigest, SHA256.HashData(File.ReadAllBytes(archive)));
            output.WriteLine("RESTORE-NODDL-QUALIFIED:" + Path.GetFileName(archive) + ":" + observed.Schema.ProfileId + ":" + Convert.ToHexStringLower(archiveDigest));
        }
        finally { Directory.Delete(root, true); }
    }
}
