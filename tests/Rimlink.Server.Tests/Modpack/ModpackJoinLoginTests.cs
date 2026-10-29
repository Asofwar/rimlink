using Google.Protobuf;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Rimlink.Server.Tests.Support;
using Xunit;
using static Rimlink.Server.Tests.Support.GrpcTestExtensions;

namespace Rimlink.Server.Tests.Modpack;

/// <summary>Logins against a modpack with file hashes, an order and shared mod settings.</summary>
public class ModpackJoinLoginTests
{
    private static readonly byte[] ServerSettings = "<SettingsBlock><ModSettings><points>2</points></ModSettings></SettingsBlock>"u8.ToArray();

    private static ServerFixture Server() => new ServerFixture()
        .WithSetting("Auth:AllowPassword", "true")
        .WithSetting("Auth:AdminLogins:0", "admin");

    /// <summary>Modpack core → rimlink → a.one (files hash "aaa") → b.two; settings shared for a.one's OneMod.</summary>
    private static async Task Prepare(ServerFixture fixture, int configs = 1, int configBytes = 0)
    {
        var admin = await fixture.RegisterAndLogin("admin");
        var modpack = new Protocol.V1.Modpack.ModpackClient(fixture.CreateChannel());
        var pack = new ModpackList();
        string[] ids = ["ludeon.rimworld", ModContent.RimlinkPackageId, "a.one", "b.two"];
        for (int i = 0; i < ids.Length; i++)
            pack.Entries.Add(new ModpackEntry { Position = i, PackageId = ids[i], Kind = EntryKind.Required, FilesHash = ids[i] == "a.one" ? "aaa" : "" });
        await modpack.SetAsync(pack, Bearer(admin.SessionToken));
        var list = new ModConfigList();
        for (int i = 0; i < configs; i++)
        {
            byte[] content = configBytes == 0 ? ServerSettings : Enumerable.Repeat((byte)('a' + i % 26), configBytes).ToArray();
            list.Configs.Add(new ModConfig { PackageId = "a.one", Handle = i == 0 ? "OneMod" : "OneMod" + i, Content = ByteString.CopyFrom(content) });
        }
        await modpack.SetConfigsAsync(list, Bearer(admin.SessionToken));
        await fixture.RegisterAndLogin("nova");
    }

    private static LoginRequest NewClient(string[] order, string filesHash = "aaa", string settingsHash = "")
    {
        var request = PasswordLogin("nova", "pw");
        for (int i = 0; i < order.Length; i++)
            request.Mods.Add(new ModRef { PackageId = order[i], Position = i, FilesHash = order[i] == "a.one" ? filesHash : "h" });
        request.ModSettings.Add(new ModSettingsHash { PackageId = ModContent.RimlinkPackageId, Handle = "RimlinkMod", Hash = ModContent.NoSettings });
        request.ModSettings.Add(new ModSettingsHash { PackageId = "a.one", Handle = "OneMod", Hash = settingsHash.Length > 0 ? settingsHash : ModContent.Hash(ServerSettings) });
        return request;
    }

    private static readonly string[] Order = ["ludeon.rimworld", ModContent.RimlinkPackageId, "a.one", "b.two"];

    [Fact]
    public async Task OldClientLogsInDespiteHashesOrderAndSettings()
    {
        using var fixture = Server();
        await Prepare(fixture);
        var request = PasswordLogin("nova", "pw");
        foreach (string id in new[] { "b.two", "a.one", ModContent.RimlinkPackageId, "ludeon.rimworld" }) request.Mods.Add(new ModRef { PackageId = id });

        var reply = await new Protocol.V1.Auth.AuthClient(fixture.CreateChannel()).LoginAsync(request);

        Assert.NotEmpty(reply.SessionToken);
    }

    [Fact]
    public async Task MatchingNewClientLogsIn()
    {
        using var fixture = Server();
        await Prepare(fixture);

        var reply = await new Protocol.V1.Auth.AuthClient(fixture.CreateChannel()).LoginAsync(NewClient(Order));

        Assert.NotEmpty(reply.SessionToken);
    }

    [Fact]
    public async Task WrongOrderIsRefusedWithOrderOnly()
    {
        using var fixture = Server();
        await Prepare(fixture);

        var reply = await new Protocol.V1.Auth.AuthClient(fixture.CreateChannel()).LoginAsync(NewClient(["ludeon.rimworld", ModContent.RimlinkPackageId, "b.two", "a.one"]));

        Assert.Empty(reply.SessionToken);
        Assert.True(reply.ModpackDiff.OrderDiffers);
        Assert.Empty(reply.ModpackDiff.Missing);
        Assert.Empty(reply.ModpackDiff.Files);
        Assert.Empty(reply.ModpackDiff.Configs);
    }

    [Fact]
    public async Task DifferentFilesAreRefused()
    {
        using var fixture = Server();
        await Prepare(fixture);

        var reply = await new Protocol.V1.Auth.AuthClient(fixture.CreateChannel()).LoginAsync(NewClient(Order, filesHash: "zzz"));

        Assert.Equal(["a.one"], reply.ModpackDiff.Files.Select(f => f.PackageId));
    }

    [Fact]
    public async Task DifferentSettingsBringTheServerFile()
    {
        using var fixture = Server();
        await Prepare(fixture);

        var reply = await new Protocol.V1.Auth.AuthClient(fixture.CreateChannel()).LoginAsync(NewClient(Order, settingsHash: "different"));

        Assert.Empty(reply.SessionToken);
        var config = Assert.Single(reply.ModpackDiff.Configs);
        Assert.Equal(("a.one", "OneMod"), (config.PackageId, config.Handle));
        Assert.Equal(ServerSettings, config.Content.ToByteArray());
        Assert.Equal(ModContent.Hash(ServerSettings), config.Hash);
    }

    [Fact]
    public async Task RegisterChecksTheSettingsToo()
    {
        using var fixture = Server();
        var admin = await fixture.RegisterAndLogin("admin");
        var modpack = new Protocol.V1.Modpack.ModpackClient(fixture.CreateChannel());
        var pack = new ModpackList();
        pack.Entries.Add(new ModpackEntry { Position = 0, PackageId = ModContent.RimlinkPackageId });
        pack.Entries.Add(new ModpackEntry { Position = 1, PackageId = "a.one" });
        await modpack.SetAsync(pack, Bearer(admin.SessionToken));
        var configs = new ModConfigList();
        configs.Configs.Add(new ModConfig { PackageId = "a.one", Handle = "OneMod", Content = ByteString.CopyFrom(ServerSettings) });
        await modpack.SetConfigsAsync(configs, Bearer(admin.SessionToken));
        var register = new RegisterRequest { Login = "nova", Password = "pw", PlayerName = "Nova" };
        register.Mods.Add(new ModRef { PackageId = ModContent.RimlinkPackageId, Position = 0, FilesHash = "h" });
        register.Mods.Add(new ModRef { PackageId = "a.one", Position = 1, FilesHash = "h" });
        register.ModSettings.Add(new ModSettingsHash { PackageId = "a.one", Handle = "OneMod", Hash = ModContent.NoSettings });

        var reply = await new Protocol.V1.Auth.AuthClient(fixture.CreateChannel()).RegisterAsync(register);

        Assert.Empty(reply.SessionToken);
        Assert.Single(reply.ModpackDiff.Configs);
    }

    [Fact]
    public async Task ADiffWithSettingsAtTheLimitFitsTheReply()
    {
        using var fixture = Server();
        await Prepare(fixture, configs: Limits.MaxModConfigs, configBytes: Limits.MaxModConfigsTotalBytes / Limits.MaxModConfigs);
        var request = NewClient(Order);
        request.ModSettings.Clear();
        for (int i = 0; i < Limits.MaxModConfigs; i++)
            request.ModSettings.Add(new ModSettingsHash { PackageId = "a.one", Handle = i == 0 ? "OneMod" : "OneMod" + i, Hash = ModContent.NoSettings });

        var reply = await new Protocol.V1.Auth.AuthClient(fixture.CreateChannel()).LoginAsync(request);

        Assert.Equal(Limits.MaxModConfigs, reply.ModpackDiff.Configs.Count);
        Assert.True(reply.CalculateSize() < Limits.MaxMessageBytes, $"login reply is {reply.CalculateSize()} bytes");
    }
}
