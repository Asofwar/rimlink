using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Admin;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Application.Saves;
using Rimlink.Server.Application.Sessions;
using Rimlink.Server.Application.World;
using Rimlink.Server.Backup;
using Rimlink.Server.Tests.Outposts;
using Rimlink.Server.Tests.Support;
using Xunit;
using ZstdSharp;

namespace Rimlink.Server.Tests.Backup;

public sealed class OutpostBackupHistoryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Backup_restores_two_retired_worlds_and_refuses_missing_original_delta_base(bool missingBase)
    {
        using var f = await LifecycleFixture.Create();
        await OutpostLifecycleTests.CloseAndReceive(f);
        var shared = f.H.F.H.H;
        var pre = f.H.F.Pre;
        var files = shared.Files;
        byte[] raw = SaveFixtures.Decompress(pre.Packed);
        await using (var held = await shared.Authority.AcquireAsync([1], default))
        {
            files.WriteAtomically(files.BasePath(1, pre.Version.Number), pre.Packed);
            files.WriteAtomically(files.DeltaPath(1, pre.Version.Number, pre.Version.Number), SaveDelta.Compress(raw, raw));
            File.Delete(files.FullPath(1, pre.Version.Number));
        }
        long archived = await f.Archive();
        var archiveIntent = OutpostLifecycleEvidence.Intent(await f.H.Db.OutpostLifecycleOperations.AsNoTracking().SingleAsync(o => o.Kind == "Archive"));
        Assert.Contains(archiveIntent.Files, file => file.Name == Path.GetFileName(files.BasePath(1, pre.Version.Number)));
        Assert.Contains(archiveIntent.Files, file => file.Name == Path.GetFileName(files.DeltaPath(1, pre.Version.Number, pre.Version.Number)));
        string originalArchive = Path.Combine(files.EvidenceArchiveDirectory(archiveIntent.Directory), "1");
        using var scope = f.Host.Services.CreateScope();
        Assert.IsType<Rimlink.Server.Application.World.RestoreOutcome.Restored>(
            await scope.ServiceProvider.GetRequiredService<ColonyArchive>().Restore(archived, "test"));
        var operations = scope.ServiceProvider.GetRequiredService<AdminOperations>();
        var worlds = scope.ServiceProvider.GetRequiredService<WorldService>();
        var admin = new Session { Token = "fixture", AccountId = 1, PlayerId = 1,
            Role = Rimlink.Server.Storage.Entities.Role.Admin, Ip = "127.0.0.1", PlayerName = "Nova" };
        var retiredWorlds = new HashSet<string> { f.H.Scope.WorldInstanceId };
        for (int generation = 1; generation <= 2; generation++)
        {
            await operations.RecreateWorld(Actor.Api);
            var world = await worlds.Create(new WorldParams { Seed = "Rimlink" }, [], admin);
            if (generation == 1) retiredWorlds.Add(world.InfluenceInstanceId);
            var metadata = f.H.F.H.Metadata(); metadata.Registry.Clear();
            metadata.Scope.WorldInstanceId = world.InfluenceInstanceId;
            metadata.Scope.SaveGeneration = generation;
            var fresh = f.H.F.H.Snapshot(metadata);
            fresh.Version.Number = 1; // Same physical filename in three different historical worlds.
            await f.H.F.H.Accept(fresh);
        }
        Assert.Equal(2, retiredWorlds.Count);
        Assert.Equal(2, await f.H.Db.OutpostTransitions.CountAsync(t => t.Kind == "WorldBind"));
        var links = await f.H.Db.OutpostSnapshotLinks.AsNoTracking().OrderBy(l => l.SnapshotId).ToArrayAsync();
        Assert.All(retiredWorlds, world => Assert.Contains(links, l => l.WorldId == world));
        await OutpostLifecycleTests.AssertOriginalEvidence(f, links);
        var original = Assert.Single(links, l => l.SnapshotId == pre.Version.SnapshotId);
        var resolved = await new OutpostLifecycleEvidence(f.H.Db, files).ResolveSnapshotAsync(original, default);
        Assert.NotNull(resolved.Archive);
        string basePath = Path.Combine(originalArchive, Path.GetFileName(files.BasePath(1, pre.Version.Number)));
        string deltaPath = Path.Combine(originalArchive, Path.GetFileName(files.DeltaPath(1, pre.Version.Number, pre.Version.Number)));
        Assert.True(File.Exists(basePath)); Assert.True(File.Exists(deltaPath));
        string relativeBase = Path.GetRelativePath(shared.Data.Root, basePath).Replace(Path.DirectorySeparatorChar, '/');
        string relativeDelta = Path.GetRelativePath(shared.Data.Root, deltaPath).Replace(Path.DirectorySeparatorChar, '/');
        string archive = await OutpostBackupTests.Create(f);
        var entries = OutpostBackupTests.Read(archive);
        Assert.Equal(File.ReadAllBytes(basePath), entries[relativeBase]);
        Assert.Equal(File.ReadAllBytes(deltaPath), entries[relativeDelta]);
        var evidenceFiles = Directory.EnumerateFiles(shared.Data.Archive, "*", SearchOption.AllDirectories)
            .ToDictionary(p => Path.GetRelativePath(shared.Data.Root, p).Replace(Path.DirectorySeparatorChar, '/'), File.ReadAllBytes);
        foreach (var (path, bytes) in evidenceFiles)
            Assert.Equal(SHA256.HashData(bytes), SHA256.HashData(entries[path]));
        var procedure = new OutpostRestoreProcedure();
        if (missingBase)
        {
            entries.Remove(relativeBase);
            var manifest = JsonSerializer.Deserialize<OutpostBackupManifest>(entries[OutpostBackupManifest.FileName], OutpostBackupManifest.Json)!;
            entries[OutpostBackupManifest.FileName] = (manifest with { Files = manifest.Files.Where(p => p.Path != relativeBase).ToArray() }).Encode();
            string invalid = Path.Combine(shared.Root, "missing-original-base.tar.zst");
            using (var file = File.Create(invalid))
            using (var zstd = new CompressionStream(file))
            using (var tar = new TarWriter(zstd, TarEntryFormat.Pax))
                foreach (var (path, bytes) in entries)
                {
                    using var content = new MemoryStream(bytes, writable: false);
                    tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, path) { DataStream = content });
                }
            string before = await f.Cut();
            var error = await Record.ExceptionAsync(() => procedure.InspectAsync(invalid, shared.Data.Root,
                Path.GetDirectoryName(shared.Anchor.DatabasePath)!, default));
            // Both original lifecycle locators and snapshot links require this base; whichever validator reaches it first must refuse.
            if (error is FileNotFoundException missing)
                Assert.Equal(Path.GetFileName(basePath), Path.GetFileName(missing.FileName));
            else
                Assert.Equal("restore-inspection-evidence-invalid: snapshot-stored-bytes-unavailable", Assert.IsType<InvalidDataException>(error).Message);
            Assert.Equal(before, await f.Cut());
            Assert.Equal(new OutpostStartupResult(OutpostStartupState.Ready), await shared.Authority.OpenAsync(default));
            return;
        }
        string originalLinks = JsonSerializer.Serialize(links);
        var request = await OutpostRestoreTests.Request(f, procedure, archive);
        f.Host.Dispose();
        var result = await procedure.ExecuteAsync(request, default);
        Assert.Equal((Rimlink.Server.Backup.RestoreOutcome.Completed, ""), (result.Outcome, result.Reason));
        f.H.Db.ChangeTracker.Clear();
        Assert.Equal(originalLinks, JsonSerializer.Serialize(await f.H.Db.OutpostSnapshotLinks.AsNoTracking().OrderBy(l => l.SnapshotId).ToArrayAsync()));
        await OutpostLifecycleTests.AssertOriginalEvidence(f, links);
        foreach (var (path, bytes) in evidenceFiles)
            Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(shared.Data.Root, path)));
        Assert.Equal(new OutpostStartupResult(OutpostStartupState.Ready), await shared.Authority.OpenAsync(default));
        File.WriteAllBytes(basePath, [1, 2, 3]);
        Assert.Equal(OutpostStartupState.Maintenance, (await shared.Authority.OpenAsync(default)).State);
    }
}
