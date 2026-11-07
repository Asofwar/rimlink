using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Backup;
using Rimlink.Server.Tests.Outposts;
using Xunit;
using ZstdSharp;

namespace Rimlink.Server.Tests.Backup;

public sealed class OutpostBackupFormatInspectionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Inspect_admits_only_the_actual_chain_format_and_schema_of_the_cut(bool activated)
    {
        using var f = await LifecycleFixture.Create(); var h = f.H.F.H.H;
        if (activated) await Activate(f);
        string archive = await OutpostBackupTests.Create(f); var original = OutpostBackupTests.Read(archive);
        string target = Path.Combine(h.Root, "format-inspect-target"), anchor = Path.Combine(h.Root, "format-inspect-anchor");
        string before = await f.Cut();
        var result = await new OutpostRestoreProcedure().InspectAsync(archive, target, anchor, default);
        Assert.Equal(activated ? OutpostDomainFormat.V2 : OutpostDomainFormat.V1, result.Manifest.Domain!.Format);
        Assert.Equal(SHA256.HashData(original[OutpostBackupManifest.FileName]), result.ManifestDigest);
        Assert.Equal(before, await f.Cut()); Assert.False(Directory.Exists(target)); Assert.False(Directory.Exists(anchor));
    }

    [Fact]
    public async Task Exact_legacy_manifest_implies_v1_only_after_real_prefix_and_profile_verification()
    {
        using var f = await LifecycleFixture.Create(); var h = f.H.F.H.H;
        var entries = OutpostBackupTests.Read(await OutpostBackupTests.Create(f));
        var json = JsonNode.Parse(entries[OutpostBackupManifest.FileName])!.AsObject(); json["version"] = 1; json.Remove("domain");
        entries[OutpostBackupManifest.FileName] = JsonSerializer.SerializeToUtf8Bytes(json);
        string archive = Write(h.Root, entries); byte[] original = File.ReadAllBytes(archive); string before = await f.Cut();
        var result = await new OutpostRestoreProcedure().InspectAsync(archive, Path.Combine(h.Root,"legacy-target"), Path.Combine(h.Root,"legacy-anchor"), default);
        Assert.Equal(1, result.Manifest.Version); Assert.Null(result.Manifest.Domain);
        Assert.Equal(SHA256.HashData(entries[OutpostBackupManifest.FileName]), result.ManifestDigest);
        Assert.Equal(original, File.ReadAllBytes(archive)); Assert.Equal(before, await f.Cut());
    }

    [Theory]
    [InlineData("forged-legacy")]
    [InlineData("wrong-format")]
    [InlineData("other-qualified-profile")]
    [InlineData("changed-domain-digest")]
    public async Task Declared_format_profile_or_legacy_label_never_substitutes_for_the_real_cut(string change)
    {
        using var f = await LifecycleFixture.Create(); var h = f.H.F.H.H; await Activate(f);
        var entries = OutpostBackupTests.Read(await OutpostBackupTests.Create(f));
        var json = JsonNode.Parse(entries[OutpostBackupManifest.FileName])!.AsObject();
        switch (change)
        {
            case "forged-legacy": json["version"] = 1; json.Remove("domain"); break;
            case "wrong-format": json["domain"]!["format"] = 1; break;
            case "changed-domain-digest": json["domainDigest"] = Convert.ToBase64String(new byte[32]); break;
            case "other-qualified-profile":
                string profile = json["domain"]!["schema"]!["profileId"]!.GetValue<string>().EndsWith("/v2-lf-lf", StringComparison.Ordinal) ? "v2-crlf-crlf" : "v2-lf-lf";
                using (var resource = typeof(OutpostDomainSchema).Assembly.GetManifestResourceStream("Rimlink.OutpostSchema." + profile + ".json"))
                {
                    Assert.NotNull(resource); json["domain"]!["schema"]!["profileId"] = "rimlink-outposts-schema/" + profile;
                    json["domain"]!["schema"]!["digest"] = Convert.ToBase64String(SHA256.HashData(resource));
                }
                break;
        }
        entries[OutpostBackupManifest.FileName] = JsonSerializer.SerializeToUtf8Bytes(json);
        string archive = Write(h.Root, entries); byte[] original = File.ReadAllBytes(archive); string before = await f.Cut();
        string target = Path.Combine(h.Root,"refused-target"), anchor = Path.Combine(h.Root,"refused-anchor");
        await Assert.ThrowsAsync<InvalidDataException>(() => new OutpostRestoreProcedure().InspectAsync(archive, target, anchor, default));
        Assert.Equal(original, File.ReadAllBytes(archive)); Assert.Equal(before, await f.Cut());
        Assert.False(Directory.Exists(target)); Assert.False(Directory.Exists(anchor));
    }

    private static async Task Activate(LifecycleFixture f)
    {
        var h = f.H.F.H.H; var command = await OutpostAdminActivationTests.CommandForCurrent(h);
        await OutpostAdminActivationTests.Service(h).ActivateDomainFormatAsync(command.Intent.Actor, command, default);
    }

    internal static string Write(string root, Dictionary<string, byte[]> entries)
    {
        string path = Path.Combine(root, "format-" + Guid.NewGuid().ToString("N") + ".tar.zst");
        using var output = File.Create(path); using var zstd = new CompressionStream(output); using var tar = new TarWriter(zstd, TarEntryFormat.Pax);
        foreach (var entry in entries)
        {
            using var content = new MemoryStream(entry.Value, writable: false);
            tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, entry.Key) { DataStream = content });
        }
        return path;
    }
}
