using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Rimlink.Server.Backup;
using Rimlink.Server.Tests.Outposts;
using Xunit;
using ZstdSharp;

namespace Rimlink.Server.Tests.Backup;

public class OutpostRestoreInspectionTests
{
    [Fact]
    public async Task Inspect_verifies_real_cut_without_creating_destination_or_anchor()
    {
        using var f = await LifecycleFixture.Create();
        string archive = await OutpostBackupTests.Create(f);
        string target = Path.Combine(f.H.F.H.H.Root, "uncreated-destination");
        string anchor = Path.Combine(f.H.F.H.H.Root, "uncreated-anchor");
        string before = await f.Cut();
        var result = await new OutpostRestoreProcedure().InspectAsync(archive, target, anchor, default);
        Assert.Equal(SHA256.HashData(File.ReadAllBytes(archive)), result.ArchiveDigest);
        Assert.Equal(SHA256.HashData(OutpostBackupTests.Read(archive)[OutpostBackupManifest.FileName]), result.ManifestDigest);
        Assert.Equal(RestoreAnchorState.Unavailable, result.Destination.AnchorState);
        Assert.Null(result.Destination.External);
        Assert.False(Directory.Exists(target));
        Assert.False(Directory.Exists(anchor));
        Assert.Equal(before, await f.Cut());
    }

    [Fact]
    public async Task Inspect_observes_external_witness_without_recovering_or_writing_anchor()
    {
        using var f = await LifecycleFixture.Create();
        string archive = await OutpostBackupTests.Create(f);
        string anchor = Path.GetDirectoryName(f.H.F.H.H.Anchor.DatabasePath)!;
        var before = Directory.GetFiles(anchor).ToDictionary(path => Path.GetFileName(path)!, File.ReadAllBytes);
        var result = await new OutpostRestoreProcedure().InspectAsync(archive, f.H.F.H.H.Data.Root, anchor, default);
        Assert.Equal(RestoreAnchorState.Committed, result.Destination.AnchorState);
        Assert.Equal(result.Manifest.Authority.HeadId, result.Destination.External!.Committed.Id);
        foreach (var pair in before) Assert.Equal(pair.Value, File.ReadAllBytes(Path.Combine(anchor, pair.Key!)));
    }

    [Fact]
    public async Task Inspect_never_substitutes_backup_cut_for_newer_external_witness()
    {
        using var f = await LifecycleFixture.Create();
        string archive = await OutpostBackupTests.Create(f);
        var cut = JsonSerializer.Deserialize<OutpostBackupManifest>(OutpostBackupTests.Read(archive)[OutpostBackupManifest.FileName], OutpostBackupManifest.Json)!;
        f.H.F.Time.Advance(TimeSpan.FromHours(1));
        await f.H.Service.StateAsync(1, new Rimlink.Protocol.V1.OutpostStateQuery { Scope = f.H.Scope.Clone() }, default);
        string anchor = Path.GetDirectoryName(f.H.F.H.H.Anchor.DatabasePath)!;
        var result = await new OutpostRestoreProcedure().InspectAsync(archive, f.H.F.H.H.Data.Root, anchor, default);
        Assert.Equal(cut.Authority.HeadId, result.Manifest.Authority.HeadId);
        Assert.Equal(RestoreAnchorState.Committed, result.Destination.AnchorState);
        Assert.NotEqual(cut.Authority.HeadId, result.Destination.External!.Committed.Id);
        Assert.Equal(20, (await f.H.Row()).Stock);
    }

    [Fact]
    public async Task Inspect_does_not_hot_recover_or_rewrite_external_journal()
    {
        using var f = await LifecycleFixture.Create();
        string archive = await OutpostBackupTests.Create(f);
        string anchor = Path.GetDirectoryName(f.H.F.H.H.Anchor.DatabasePath)!;
        string journal = f.H.F.H.H.Anchor.DatabasePath + "-journal";
        byte[] original = File.ReadAllBytes(journal);
        try
        {
            byte[] hot = (byte[])original.Clone(); hot[0] = 0xD9;
            File.WriteAllBytes(journal, hot);
            byte[] database = File.ReadAllBytes(f.H.F.H.H.Anchor.DatabasePath);
            var result = await new OutpostRestoreProcedure().InspectAsync(archive, f.H.F.H.H.Data.Root, anchor, default);
            Assert.Equal(RestoreAnchorState.Unavailable, result.Destination.AnchorState);
            Assert.Equal("anchor-journal-not-proven-cold", result.Destination.Reason);
            Assert.Null(result.Destination.External);
            Assert.Equal(hot, File.ReadAllBytes(journal));
            Assert.Equal(database, File.ReadAllBytes(f.H.F.H.H.Anchor.DatabasePath));
        }
        finally { File.WriteAllBytes(journal, original); }
    }

    [Theory]
    [InlineData("missing-file")]
    [InlineData("changed-file")]
    [InlineData("unlisted-file")]
    [InlineData("duplicate-file")]
    [InlineData("path-escape")]
    [InlineData("link")]
    [InlineData("manifest-too-large")]
    [InlineData("manifest-duplicate-property")]
    [InlineData("missing-required-proof")]
    [InlineData("missing-snapshot-with-valid-inventory")]
    [InlineData("changed-domain-with-valid-inventory")]
    [InlineData("changed-chain-with-valid-inventory")]
    public async Task Inspect_rejects_untrusted_archive_before_any_destination_effect(string kind)
    {
        using var f = await LifecycleFixture.Create();
        var entries = OutpostBackupTests.Read(await OutpostBackupTests.Create(f));
        var manifest = JsonSerializer.Deserialize<OutpostBackupManifest>(entries[OutpostBackupManifest.FileName], OutpostBackupManifest.Json)!;
        string proof = entries.Keys.First(p => p.StartsWith("saves/", StringComparison.Ordinal) && p.EndsWith(".zst", StringComparison.Ordinal));
        if (kind is "missing-file" or "missing-snapshot-with-valid-inventory") entries.Remove(proof);
        if (kind == "changed-file") entries[proof] = "not the original compressed snapshot"u8.ToArray();
        if (kind == "unlisted-file") entries.Add("saves/extra.rws.zst", [1, 2, 3]);
        if (kind == "manifest-too-large") entries[OutpostBackupManifest.FileName] = new byte[OutpostBackupManifest.MaximumBytes + 1];
        if (kind == "manifest-duplicate-property") entries[OutpostBackupManifest.FileName] = "{\"version\":1,\"version\":1}"u8.ToArray();
        if (kind == "missing-required-proof") entries[OutpostBackupManifest.FileName] = (manifest with { Proofs = [] }).Encode();
        if (kind == "missing-snapshot-with-valid-inventory")
            entries[OutpostBackupManifest.FileName] = (manifest with { Files = manifest.Files.Where(p => p.Path != proof).ToArray() }).Encode();
        if (kind is "changed-domain-with-valid-inventory" or "changed-chain-with-valid-inventory")
        {
            string database = Path.Combine(f.H.F.H.H.Root, "tampered-backup.db");
            File.WriteAllBytes(database, entries[BackupArchive.DatabaseFile]);
            using (var connection = new SqliteConnection($"Data Source={database};Pooling=False"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                string? immutableGuard = null;
                if (kind == "changed-chain-with-valid-inventory")
                {
                    // Tamper ONLY the extracted test copy; restore its exact schema before inspection.
                    command.CommandText = "SELECT sql FROM sqlite_schema WHERE name='outpost_transitions_no_update'";
                    immutableGuard = Assert.IsType<string>(command.ExecuteScalar());
                    command.CommandText = "DROP TRIGGER outpost_transitions_no_update"; command.ExecuteNonQuery();
                }
                command.CommandText = kind == "changed-domain-with-valid-inventory" ? "UPDATE outposts SET Stock=123"
                    : "UPDATE outpost_transitions SET Digest=zeroblob(32) WHERE Id=(SELECT TransitionId FROM outpost_authority)";
                Assert.True(command.ExecuteNonQuery() > 0);
                if (immutableGuard is not null) { command.CommandText = immutableGuard; command.ExecuteNonQuery(); }
            }
            entries[BackupArchive.DatabaseFile] = File.ReadAllBytes(database);
            entries[OutpostBackupManifest.FileName] = (manifest with { Files = manifest.Files.Select(p => p.Path != BackupArchive.DatabaseFile ? p
                : p with { Length = entries[p.Path].LongLength, Sha256 = SHA256.HashData(entries[p.Path]) }).ToArray() }).Encode();
        }
        string archive = Path.Combine(f.H.F.H.H.Root, "untrusted.tar.zst");
        using (var file = File.Create(archive))
        using (var zstd = new CompressionStream(file))
        using (var tar = new TarWriter(zstd, TarEntryFormat.Pax))
        {
            foreach (var pair in entries) Write(tar, pair.Key, pair.Value);
            if (kind == "duplicate-file") Write(tar, proof, [0]);
            if (kind == "path-escape") Write(tar, "saves/../../outside", [0]);
            if (kind == "link") tar.WriteEntry(new PaxTarEntry(TarEntryType.SymbolicLink, "saves/link") { LinkName = "../../outside" });
        }
        string target = Path.Combine(f.H.F.H.H.Root, "untouched");
        Directory.CreateDirectory(target);
        string sentinel = Path.Combine(target, "sentinel"); File.WriteAllText(sentinel, "old set");
        string anchor = Path.GetDirectoryName(f.H.F.H.H.Anchor.DatabasePath)!;
        string before = await f.Cut();
        await Assert.ThrowsAnyAsync<InvalidDataException>(() => new OutpostRestoreProcedure().InspectAsync(archive, target, anchor, default));
        Assert.Equal("old set", File.ReadAllText(sentinel));
        Assert.Single(Directory.GetFiles(target));
        Assert.Equal(before, await f.Cut());
    }

    private static void Write(TarWriter tar, string path, byte[] bytes)
    {
        using var content = new MemoryStream(bytes, writable: false);
        tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, path) { DataStream = content });
    }
}
