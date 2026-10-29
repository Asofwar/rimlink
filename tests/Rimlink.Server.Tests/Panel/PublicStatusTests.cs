using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Rimlink.Server.Storage;
using Rimlink.Server.Tests.Support;
using Xunit;

namespace Rimlink.Server.Tests.Panel;

public class PublicStatusTests
{
    private readonly ManualTimeProvider clock = new();

    private ServerFixture Server() => new ServerFixture()
        .WithSetting("Auth:AllowPassword", "true")
        .WithSetting("Admin:ApiToken", AdminRestExtensions.ApiToken)
        .WithServices(s => s.AddSingleton<TimeProvider>(clock));

    [Fact]
    public async Task PublicStatusNeedsNoAuth()
    {
        using var fixture = Server();

        var response = await fixture.CreateClient().GetAsync("/api/public/status");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Rimlink", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("serverName").GetString());
    }

    [Fact]
    public async Task PublicStatusHasNoAddressesOrSteamIds()
    {
        using var fixture = Server().WithSetting("Panel:PublicAddress", "play.example:25560");
        await fixture.RegisterAndLogin("nova", playerName: "Nova");
        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RimlinkDbContext>();
            (await db.Accounts.SingleAsync(a => a.Login == "nova")).SteamId = 76561198000000001;
            await db.SaveChangesAsync();
        }

        string body = await fixture.CreateClient().GetStringAsync("/api/public/status");

        Assert.DoesNotContain("127.0.0.1", body);
        Assert.DoesNotContain("76561198000000001", body);
        var status = JsonDocument.Parse(body).RootElement;
        Assert.Equal("play.example:25560", status.GetProperty("address").GetString());
        Assert.Equal("Nova", status.GetProperty("players").EnumerateArray().Single().GetProperty("name").GetString());
    }

    [Fact]
    public async Task PlayerNamesCanBeHidden()
    {
        using var fixture = Server().WithSetting("Panel:PublicPlayerNames", "false");
        await fixture.RegisterAndLogin("nova", playerName: "Nova");

        var status = await fixture.CreateClient().GetFromJsonAsync<JsonElement>("/api/public/status");

        Assert.Equal(JsonValueKind.Null, status.GetProperty("players").ValueKind);
        Assert.Equal(1, status.GetProperty("playersOnline").GetInt32());
    }

    [Fact]
    public async Task PublicStatusCanBeTurnedOff()
    {
        using var fixture = Server().WithSetting("Panel:Public", "false");

        Assert.Equal(HttpStatusCode.NotFound, (await fixture.CreateClient().GetAsync("/api/public/status")).StatusCode);
    }

    [Fact]
    public async Task ModpackAndLoginModesAreShown()
    {
        using var fixture = Server().WithSetting("Server:ServerPassword", "x");
        var list = new { entries = new[] { new { position = 0, packageId = "Ludeon.RimWorld", version = "1.7", workshopId = 0UL, url = "", kind = "required" } } };
        Assert.Equal(HttpStatusCode.OK, (await fixture.Rest().PutAsJsonAsync("/api/admin/modpack", list)).StatusCode);

        var status = await fixture.CreateClient().GetFromJsonAsync<JsonElement>("/api/public/status");

        var entry = status.GetProperty("modpack").EnumerateArray().Single();
        Assert.Equal("ludeon.rimworld", entry.GetProperty("packageId").GetString());
        Assert.Equal("required", entry.GetProperty("kind").GetString());
        Assert.True(status.GetProperty("passwordRequired").GetBoolean());
        Assert.True(status.GetProperty("passwordLogin").GetBoolean());
        Assert.False(status.GetProperty("steamLogin").GetBoolean());
        Assert.Matches("^[0-9A-F]{64}$", status.GetProperty("certificateFingerprint").GetString()!);
    }

    [Fact]
    public async Task PublicStatusIsCachedFor15Seconds()
    {
        using var fixture = Server();
        var client = fixture.CreateClient();

        Assert.Equal(0, (await client.GetFromJsonAsync<JsonElement>("/api/public/status")).GetProperty("playersOnline").GetInt32());
        await fixture.RegisterAndLogin("nova", playerName: "Nova");
        Assert.Equal(0, (await client.GetFromJsonAsync<JsonElement>("/api/public/status")).GetProperty("playersOnline").GetInt32());

        clock.Advance(TimeSpan.FromSeconds(16));

        Assert.Equal(1, (await client.GetFromJsonAsync<JsonElement>("/api/public/status")).GetProperty("playersOnline").GetInt32());
    }
}
