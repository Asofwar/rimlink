using System.Text.Json;
using Google.Protobuf;
using Rimlink.Server.Backup;
using Rimlink.Server.Tests.Outposts;
using Xunit;

namespace Rimlink.Server.Tests.Backup;

public sealed class OutpostRestoreRequestJsonTests
{
    public sealed record Bytes(ByteString Value);
    [Fact]
    public async Task Existing_anchor_bytes_keep_the_numeric_array_wire_format()
    {
        var original = new Bytes(ByteString.CopyFrom([0, 255, 17]));
        Assert.Equal("{\"value\":[0,255,17]}", OutpostRestoreRequestJson.Serialize(original));
        Assert.Equal(original, await Read<Bytes>("{\"value\":[0,255,17]}"));
        Assert.Equal(original, await Read<Bytes>("\uFEFF{\"value\":[0,255,17]}"));
    }
    [Theory]
    [InlineData("{\"value\":\"AP8R\"}")]
    [InlineData("{\"value\":[256]}")]
    [InlineData("{\"value\":[-1]}")]
    [InlineData("{\"value\":[1.5]}")]
    [InlineData("{\"value\":[],\"value\":[]}")]
    [InlineData("{\"value\":[],\"unknown\":0}")]
    [InlineData("{\"value\":null}")]
    [InlineData("{}")]
    public async Task Request_parser_keeps_the_existing_strict_boundary(string json)
        => await Assert.ThrowsAsync<JsonException>(() => Read<Bytes>(json));

    [Fact]
    public async Task Size_depth_and_byte_count_limits_remain_bounded()
    {
        await Assert.ThrowsAsync<JsonException>(() => Read<Bytes>("{\"value\":[" + string.Join(',', Enumerable.Repeat("0", 4097)) + "]}"));
        await Assert.ThrowsAsync<JsonException>(() => Read<JsonElement>(new string('[', 33) + "0" + new string(']', 33)));
        await Assert.ThrowsAsync<InvalidDataException>(() => Read<JsonElement>(""));
        await Assert.ThrowsAsync<InvalidDataException>(() => Read<JsonElement>(new string(' ', 32 * 1024 * 1024 + 1)));
    }
    [Fact]
    public async Task A_real_cli_restore_request_roundtrip_keeps_the_exact_selection_digest()
    {
        using var f = await LifecycleFixture.Create(runtimeQualified: true);
        string archive = await OutpostBackupTests.Create(f);
        var original = await OutpostRestoreTests.Request(f, new OutpostRestoreProcedure(f.H.F.Time), archive);
        var copy = await Read<RestoreRequest>(OutpostRestoreRequestJson.Serialize(original));
        Assert.Equal(OutpostRestoreJournalCodec.SelectionDigest(original), OutpostRestoreJournalCodec.SelectionDigest(copy));
        Assert.Equal(original.ConfirmationDigest, copy.ConfirmationDigest);
    }
    private static async Task<T> Read<T>(string text)
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        try { await File.WriteAllTextAsync(path, text, new System.Text.UTF8Encoding(false)); return await OutpostRestoreRequestJson.ReadAsync<T>(path, default); }
        finally { File.Delete(path); }
    }
}
