using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Backup;
using Rimlink.Server.Tests.Outposts;
using Xunit;

namespace Rimlink.Server.Tests.Backup;

public sealed class OutpostBackupManifestCodecTests
{
    [Fact]
    public void Exact_legacy_shape_roundtrips_without_serializing_new_properties()
    {
        var original = Example(); byte[] bytes = original.Encode();
        using var document = JsonDocument.Parse(bytes); Assert.False(document.RootElement.TryGetProperty("domain", out _));
        Assert.Equal(bytes, OutpostBackupManifest.Decode(bytes).Encode());
    }

    [Theory]
    [InlineData("unknown-version")]
    [InlineData("legacy-with-domain")]
    [InlineData("v2-missing-domain")]
    [InlineData("v2-null-domain")]
    [InlineData("domain-unknown-version")]
    [InlineData("domain-unknown-format")]
    [InlineData("domain-unknown-profile")]
    [InlineData("domain-changed-profile-digest")]
    [InlineData("unknown-property")]
    [InlineData("duplicate-version")]
    [InlineData("truncated")]
    public async Task Unsupported_or_ambiguous_envelopes_never_fall_back_to_legacy(string change)
    {
        using var f = new AuthorityHarness();
        var schema = await OutpostDomainSchema.ValidateAsync(f.Db, OutpostSchemaContext.CurrentRuntime, default);
        var json = JsonNode.Parse(Example().Encode())!.AsObject();
        json["version"] = 2;
        json["domain"] = JsonSerializer.SerializeToNode(new { Version = 1, Format = OutpostDomainFormat.V2,
            Schema = new OutpostSchemaBinding(schema.ProfileId, schema.Digest.ToByteArray()) }, OutpostBackupManifest.Json);
        switch (change)
        {
            case "unknown-version": json["version"] = 3; break;
            case "legacy-with-domain": json["version"] = 1; break;
            case "v2-missing-domain": json.Remove("domain"); break;
            case "v2-null-domain": json["domain"] = null; break;
            case "domain-unknown-version": json["domain"]!["version"] = 2; break;
            case "domain-unknown-format": json["domain"]!["format"] = 3; break;
            case "domain-unknown-profile": json["domain"]!["schema"]!["profileId"] = "unqualified"; break;
            case "domain-changed-profile-digest": json["domain"]!["schema"]!["digest"] = Convert.ToBase64String(new byte[32]); break;
            case "unknown-property": json["legacyFormat"] = 1; break;
        }
        byte[] encoded = JsonSerializer.SerializeToUtf8Bytes(json);
        if (change == "duplicate-version") encoded = Encoding.UTF8.GetBytes("{\"version\":2," + Encoding.UTF8.GetString(encoded)[1..]);
        if (change == "truncated") encoded = encoded[..^1];
        Assert.Throws<InvalidDataException>(() => OutpostBackupManifest.Decode(encoded));
    }

    internal static OutpostBackupManifest Example() => new(1, new(1, Guid.NewGuid().ToString("D"), "world-1", Guid.NewGuid().ToString("D"),
        Guid.NewGuid().ToString("D"), new byte[32], Guid.NewGuid().ToString("D"), "Ready"), new DateTime(2026,11,5,0,0,0,DateTimeKind.Utc), new byte[32], [], []);
}
