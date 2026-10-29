using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Rimlink.Server.Hosting;
using Rimlink.Server.Tests.Support;
using Xunit;

namespace Rimlink.Server.Tests.Admin;

public class StatusConfigTests
{
    private const string BotToken = "bot-token-secret-0123456789";

    [Fact]
    public async Task StatusReportsPlayersWorldAndCertificate()
    {
        using var fixture = new ServerFixture().WithSetting("Auth:AllowPassword", "true").WithSetting("Admin:ApiToken", AdminRestExtensions.ApiToken);
        await fixture.RegisterAndLogin("nova", playerName: "Nova");

        var status = await fixture.Rest().GetFromJsonAsync<JsonElement>("/api/admin/status");

        Assert.Equal(1, status.GetProperty("playersOnline").GetInt32());
        Assert.Equal(1, status.GetProperty("playersTotal").GetInt32());
        Assert.True(status.GetProperty("worldExists").GetBoolean());
        Assert.Equal(ServerVersion.Current, status.GetProperty("version").GetString());
        Assert.Equal(fixture.Services.GetRequiredService<ServerCertificate>().Fingerprint, status.GetProperty("certificateFingerprint").GetString());
        Assert.True(status.GetProperty("certificateSelfSigned").GetBoolean());
        Assert.Equal(0, status.GetProperty("saves").GetProperty("count").GetInt32());
        Assert.False(status.GetProperty("economy").GetProperty("frozen").GetBoolean());
        Assert.Equal(0, status.GetProperty("raids").GetProperty("active").GetInt32());
    }

    [Fact]
    public async Task ConfigHidesSecrets()
    {
        using var fixture = new ServerFixture()
            .WithSetting("Admin:ApiToken", AdminRestExtensions.ApiToken)
            .WithSetting("Server:ServerPassword", "pw-secret-1")
            .WithSetting("Auth:SteamWebApiKey", "steam-secret-2")
            .WithToken(0, "bot", BotToken, "read", "chat");
        var rest = fixture.Rest();

        string configText = await rest.GetStringAsync("/api/admin/config");
        string statusText = await rest.GetStringAsync("/api/admin/status");

        foreach (string secret in new[] { AdminRestExtensions.ApiToken, "pw-secret-1", "steam-secret-2", BotToken })
        {
            Assert.DoesNotContain(secret, configText);
            Assert.DoesNotContain(secret, statusText);
        }
        var config = JsonDocument.Parse(configText).RootElement;
        Assert.Equal("set", config.GetProperty("server").GetProperty("serverPassword").GetString());
        Assert.Equal("set", config.GetProperty("auth").GetProperty("steamWebApiKey").GetString());
        Assert.Equal("set", config.GetProperty("admin").GetProperty("apiToken").GetString());
        Assert.Equal(0, config.GetProperty("economy").GetProperty("marketFeePercent").GetInt32());
        Assert.Equal(12, config.GetProperty("panel").GetProperty("sessionHours").GetInt32());
        var token = config.GetProperty("admin").GetProperty("tokens").EnumerateArray().Single();
        Assert.Equal("bot", token.GetProperty("name").GetString());
        Assert.Equal(["read", "chat"], token.GetProperty("scopes").EnumerateArray().Select(s => s.GetString()));
    }

    [Fact]
    public async Task StatusNeedsReadAndConfigNeedsAdmin()
    {
        using var fixture = new ServerFixture().WithSetting("Admin:ApiToken", AdminRestExtensions.ApiToken).WithToken(0, "bot", BotToken, "read");
        var bot = fixture.Rest(BotToken);

        Assert.Equal(HttpStatusCode.OK, (await bot.GetAsync("/api/admin/status")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await bot.GetAsync("/api/admin/config")).StatusCode);
    }
}
