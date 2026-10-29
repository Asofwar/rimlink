using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Google.Protobuf;
using Grpc.Core;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Rimlink.Server.Tests.Support;
using Xunit;
using static Rimlink.Server.Tests.Support.GrpcTestExtensions;

namespace Rimlink.Server.Tests.Modpack;

public class ModpackConfigsTests
{
    private const string Token = "test-admin-token";

    private static ServerFixture Server() => new ServerFixture()
        .WithSetting("Auth:AllowPassword", "true")
        .WithSetting("Auth:AdminLogins:0", "admin")
        .WithSetting("Admin:ApiToken", Token);

    /// <summary>Core, Harmony, Rimlink, then two mods below Rimlink (positions 3 and 4) and one above it.</summary>
    private static ModpackList Pack()
    {
        var list = new ModpackList();
        string[] ids = ["ludeon.rimworld", "brrainz.harmony", ModContent.RimlinkPackageId, "author.raidpoints", "author.trader"];
        for (int i = 0; i < ids.Length; i++) list.Entries.Add(new ModpackEntry { Position = i, PackageId = ids[i], Kind = EntryKind.Required });
        return list;
    }

    private static ModConfig Config(string packageId, string handle, int size = 10, byte fill = (byte)'x') =>
        new() { PackageId = packageId, Handle = handle, Content = ByteString.CopyFrom(Enumerable.Repeat(fill, size).ToArray()), Hash = "client-claim" };

    private static async Task<Protocol.V1.Modpack.ModpackClient> AdminWithPack(ServerFixture fixture, Metadata[] auth)
    {
        var admin = await fixture.RegisterAndLogin("admin");
        auth[0] = Bearer(admin.SessionToken);
        var modpack = new Protocol.V1.Modpack.ModpackClient(fixture.CreateChannel());
        await modpack.SetAsync(Pack(), auth[0]);
        return modpack;
    }

    private static ModConfigList ListOf(params ModConfig[] configs)
    {
        var list = new ModConfigList();
        list.Configs.AddRange(configs);
        return list;
    }

    [Fact]
    public async Task AdminSetsAndReadsConfigs()
    {
        using var fixture = Server();
        var auth = new Metadata[1];
        var modpack = await AdminWithPack(fixture, auth);

        await modpack.SetConfigsAsync(ListOf(Config("Author.RaidPoints", "RaidPointsMod", fill: (byte)'a'), Config("author.trader", "TraderMod", fill: (byte)'b')), auth[0]);
        var stored = await modpack.GetConfigsAsync(new Empty(), auth[0]);

        Assert.Equal(["author.raidpoints:RaidPointsMod", "author.trader:TraderMod"], stored.Configs.Select(c => $"{c.PackageId}:{c.Handle}").Order());
        Assert.All(stored.Configs, c => Assert.Equal(ModContent.Hash(c.Content.ToByteArray()), c.Hash));
    }

    [Fact]
    public async Task ReplacingTheSetDropsOldRowsAndAnEmptySetTurnsSyncOff()
    {
        using var fixture = Server();
        var auth = new Metadata[1];
        var modpack = await AdminWithPack(fixture, auth);
        await modpack.SetConfigsAsync(ListOf(Config("author.raidpoints", "A"), Config("author.trader", "B")), auth[0]);

        await modpack.SetConfigsAsync(ListOf(Config("author.trader", "B")), auth[0]);
        Assert.Equal(["author.trader"], (await modpack.GetConfigsAsync(new Empty(), auth[0])).Configs.Select(c => c.PackageId));

        await modpack.SetConfigsAsync(new ModConfigList(), auth[0]);
        Assert.Empty((await modpack.GetConfigsAsync(new Empty(), auth[0])).Configs);
    }

    public static TheoryData<string, ModConfig[]> Refusals => new()
    {
        { "not in the modpack", [Config("author.unknown", "X")] },
        { "loads before Rimlink", [Config("brrainz.harmony", "HarmonyMod")] },
        { "file too large", [Config("author.trader", "X", Limits.MaxModConfigBytes + 1)] },
        { "total too large", Enumerable.Range(0, 4).Select(i => Config("author.trader", "H" + i, 200 * 1024)).ToArray() },
        { "too many", Enumerable.Range(0, Limits.MaxModConfigs + 1).Select(i => Config("author.trader", "H" + i)).ToArray() },
    };

    [Theory]
    [MemberData(nameof(Refusals))]
    public async Task InvalidSetsAreRefusedAndNothingIsStored(string why, ModConfig[] configs)
    {
        using var fixture = Server();
        var auth = new Metadata[1];
        var modpack = await AdminWithPack(fixture, auth);
        await modpack.SetConfigsAsync(ListOf(Config("author.raidpoints", "Kept")), auth[0]);

        var ex = await Assert.ThrowsAsync<RpcException>(() => modpack.SetConfigsAsync(ListOf(configs), auth[0]).ResponseAsync);

        Assert.True(ex.ErrorDetail()?.Code == ErrorCode.ModpackConfigsInvalid, why);
        Assert.Equal(["Kept"], (await modpack.GetConfigsAsync(new Empty(), auth[0])).Configs.Select(c => c.Handle));
    }

    [Fact]
    public async Task RefusedWhenRimlinkIsNotInTheModpack()
    {
        using var fixture = Server();
        var admin = await fixture.RegisterAndLogin("admin");
        var modpack = new Protocol.V1.Modpack.ModpackClient(fixture.CreateChannel());
        var pack = new ModpackList();
        pack.Entries.Add(new ModpackEntry { Position = 0, PackageId = "author.trader" });
        await modpack.SetAsync(pack, Bearer(admin.SessionToken));

        var ex = await Assert.ThrowsAsync<RpcException>(() => modpack.SetConfigsAsync(ListOf(Config("author.trader", "X")), Bearer(admin.SessionToken)).ResponseAsync);

        Assert.Equal(ErrorCode.ModpackConfigsInvalid, ex.ErrorDetail()?.Code);
    }

    [Fact]
    public async Task PlayerCannotSetOrReadConfigs()
    {
        using var fixture = Server();
        var nova = await fixture.RegisterAndLogin("nova");
        var modpack = new Protocol.V1.Modpack.ModpackClient(fixture.CreateChannel());

        var set = await Assert.ThrowsAsync<RpcException>(() => modpack.SetConfigsAsync(new ModConfigList(), Bearer(nova.SessionToken)).ResponseAsync);
        var get = await Assert.ThrowsAsync<RpcException>(() => modpack.GetConfigsAsync(new Empty(), Bearer(nova.SessionToken)).ResponseAsync);

        Assert.Equal(StatusCode.PermissionDenied, set.StatusCode);
        Assert.Equal(StatusCode.PermissionDenied, get.StatusCode);
    }

    [Fact]
    public async Task RestPutsAndListsConfigsWithoutContent()
    {
        using var fixture = Server();
        var auth = new Metadata[1];
        await AdminWithPack(fixture, auth);
        var rest = fixture.CreateClient();
        rest.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        byte[] content = "<SettingsBlock/>"u8.ToArray();

        var put = await rest.PutAsJsonAsync("/api/admin/modpack/configs", new { configs = new[] { new { packageId = "author.trader", handle = "TraderMod", contentBase64 = Convert.ToBase64String(content) } } });
        var listed = (await rest.GetFromJsonAsync<JsonElement>("/api/admin/modpack/configs")).EnumerateArray().Single();
        var refused = await rest.PutAsJsonAsync("/api/admin/modpack/configs", new { configs = new[] { new { packageId = "author.unknown", handle = "X", contentBase64 = "" } } });

        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.Equal("author.trader", listed.GetProperty("packageId").GetString());
        Assert.Equal("TraderMod", listed.GetProperty("handle").GetString());
        Assert.Equal(ModContent.Hash(content), listed.GetProperty("hash").GetString());
        Assert.Equal(content.Length, listed.GetProperty("size").GetInt32());
        Assert.False(listed.TryGetProperty("content", out _));
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
    }

    [Fact]
    public async Task ModpackKeepsFilesHash()
    {
        using var fixture = Server();
        var admin = await fixture.RegisterAndLogin("admin");
        var modpack = new Protocol.V1.Modpack.ModpackClient(fixture.CreateChannel());
        var pack = new ModpackList();
        pack.Entries.Add(new ModpackEntry { Position = 0, PackageId = "author.trader", FilesHash = "abc123" });
        pack.Entries.Add(new ModpackEntry { Position = 1, PackageId = "author.other" });
        await modpack.SetAsync(pack, Bearer(admin.SessionToken));

        var stored = await modpack.GetAsync(new Empty(), Bearer(admin.SessionToken));

        Assert.Equal(["abc123", ""], stored.Entries.Select(e => e.FilesHash));

        var rest = fixture.CreateClient();
        rest.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        var body = new { entries = new[] { new { position = 0, packageId = "author.trader", version = "", workshopId = 0UL, url = "", kind = "required", filesHash = "def456" } } };
        Assert.Equal(HttpStatusCode.OK, (await rest.PutAsJsonAsync("/api/admin/modpack", body)).StatusCode);
        var viaRest = await rest.GetFromJsonAsync<JsonElement>("/api/admin/modpack");
        Assert.Equal("def456", viaRest.GetProperty("entries").EnumerateArray().Single().GetProperty("filesHash").GetString());
    }
}
