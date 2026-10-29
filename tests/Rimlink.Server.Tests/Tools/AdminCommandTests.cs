extern alias tools;
using System.Net.Http.Headers;
using System.Text.Json;
using Rimlink.Server.Tests.Support;
using tools::Rimlink.Tools.Commands;
using Xunit;

namespace Rimlink.Server.Tests.Tools;

/// <summary>The admin CLI's command table against a real in-process server.</summary>
public class AdminCommandTests
{
    private static ServerFixture Server() => new ServerFixture()
        .WithSetting("Auth:AllowPassword", "true")
        .WithSetting("Admin:ApiToken", AdminRestExtensions.ApiToken);

    private static RestClient Cli(ServerFixture fixture)
    {
        var http = fixture.CreateClient();
        http.BaseAddress = new Uri(http.BaseAddress!, "/api/admin/");
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AdminRestExtensions.ApiToken);
        return new RestClient(http);
    }

    [Fact]
    public async Task PlayersAndPlayerPrintJson()
    {
        using var fixture = Server();
        var nova = await fixture.RegisterAndLogin("nova", playerName: "Nova");
        using var cli = Cli(fixture);

        Assert.Contains("\"Nova\"", await AdminCommand.Run(cli, "players", ["nov"]));
        Assert.Contains("walletBalance", await AdminCommand.Run(cli, "player", [nova.Player.PlayerId.ToString()]));
    }

    [Fact]
    public async Task PlayerFreezeAndUnfreezeRun()
    {
        using var fixture = Server();
        var nova = await fixture.RegisterAndLogin("nova", playerName: "Nova");
        string id = nova.Player.PlayerId.ToString();
        using var cli = Cli(fixture);

        await AdminCommand.Run(cli, "player-freeze", [id, "dupe", "x"]);
        var frozen = JsonDocument.Parse(await AdminCommand.Run(cli, "player", [id])).RootElement;
        await AdminCommand.Run(cli, "player-unfreeze", [id]);
        var open = JsonDocument.Parse(await AdminCommand.Run(cli, "player", [id])).RootElement;

        Assert.Equal("dupe x", frozen.GetProperty("economyFreezeReason").GetString());
        Assert.Equal(JsonValueKind.Null, open.GetProperty("economyFrozenAt").ValueKind);
    }

    [Fact]
    public async Task RoleRenameSaveAllStatusAndConfigRun()
    {
        using var fixture = Server();
        var nova = await fixture.RegisterAndLogin("nova", playerName: "Nova");
        string id = nova.Player.PlayerId.ToString();
        using var cli = Cli(fixture);

        await AdminCommand.Run(cli, "role", [id, "moderator"]);
        await AdminCommand.Run(cli, "rename", [id, "Nova2", "Star", "Guild"]);
        await AdminCommand.Run(cli, "saveall", []);
        await AdminCommand.Run(cli, "chat", []);
        Assert.Contains("playersOnline", await AdminCommand.Run(cli, "status", []));
        Assert.DoesNotContain(AdminRestExtensions.ApiToken, await AdminCommand.Run(cli, "config", []));

        var card = JsonDocument.Parse(await AdminCommand.Run(cli, "player", [id])).RootElement;
        Assert.Equal("moderator", card.GetProperty("role").GetString());
        Assert.Equal("Nova2", card.GetProperty("name").GetString());
        Assert.Equal("Star Guild", card.GetProperty("factionName").GetString());
    }

    [Fact]
    public async Task BackupAndBackupGet()
    {
        using var fixture = Server();
        using var cli = Cli(fixture);
        string target = Path.Combine(fixture.DataDir, "downloaded.tar.zst");

        string name = JsonDocument.Parse(await AdminCommand.Run(cli, "backup", [])).RootElement.GetProperty("name").GetString()!;
        Assert.Contains(name, await AdminCommand.Run(cli, "backups", []));

        Assert.Equal($"saved {target}", await AdminCommand.Run(cli, "backup-get", [name, target]));
        Assert.Equal(await File.ReadAllBytesAsync(Path.Combine(fixture.DataDir, "backups", name)), await File.ReadAllBytesAsync(target));
    }

    [Fact]
    public async Task TradesAndTradeCancel()
    {
        using var fixture = Server();
        var nova = await fixture.RegisterAndLogin("nova", playerName: "Nova");
        var bob = await fixture.RegisterAndLogin("bob", playerName: "Bob");
        var trades = new Rimlink.Protocol.V1.Trades.TradesClient(fixture.CreateChannel());
        var trade = await trades.OpenAsync(SocialFixtures.OpenTrade(bob.Player.PlayerId, note: "hi"), GrpcTestExtensions.Bearer(nova.SessionToken));
        using var cli = Cli(fixture);

        Assert.Equal(trade.Id, JsonDocument.Parse(await AdminCommand.Run(cli, "trades", [nova.Player.PlayerId.ToString()])).RootElement[0].GetProperty("id").GetInt64());
        await AdminCommand.Run(cli, "trade", ["cancel", trade.Id.ToString(), "by", "cli"]);

        Assert.Equal("Cancelled", JsonDocument.Parse(await AdminCommand.Run(cli, "trades", [])).RootElement[0].GetProperty("status").GetString());
    }

    [Fact]
    public async Task UnknownCommandThrows()
    {
        using var fixture = Server();
        using var cli = Cli(fixture);

        await Assert.ThrowsAsync<InvalidOperationException>(() => AdminCommand.Run(cli, "nope", []));
    }
}
