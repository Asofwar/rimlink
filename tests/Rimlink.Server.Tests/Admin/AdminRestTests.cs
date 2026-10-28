using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Grpc.Core;
using Rimlink.Protocol.V1;
using Rimlink.Server.Tests.Support;
using Xunit;
using static Rimlink.Server.Tests.Support.GrpcTestExtensions;

namespace Rimlink.Server.Tests.Admin;

public class AdminRestTests
{
    private const string Token = "test-admin-token";

    private static ServerFixture Server() => new ServerFixture()
        .WithSetting("Auth:AllowPassword", "true")
        .WithSetting("Admin:ApiToken", Token);

    private static HttpClient Rest(ServerFixture fixture)
    {
        var client = fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        return client;
    }

    [Fact]
    public async Task MissingOrWrongTokenIs401()
    {
        using var fixture = Server();
        var anonymous = fixture.CreateClient();
        var wrong = fixture.CreateClient();
        wrong.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "nope");

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/admin/players/online")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await wrong.GetAsync("/api/admin/players/online")).StatusCode);
    }

    [Fact]
    public async Task RestIsOffWithoutConfiguredToken()
    {
        using var fixture = new ServerFixture().WithSetting("Auth:AllowPassword", "true");
        var client = fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "");

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/admin/players/online")).StatusCode);
    }

    [Fact]
    public async Task OnlineListsLoggedInPlayers()
    {
        using var fixture = Server();
        var nova = await fixture.RegisterAndLogin("nova", playerName: "Nova");

        var online = await Rest(fixture).GetFromJsonAsync<JsonElement>("/api/admin/players/online");

        Assert.Equal("Nova", online.EnumerateArray().Single().GetProperty("name").GetString());
    }

    [Fact]
    public async Task BanViaRestThenPlayerCannotLoginAndActionsLogListsIt()
    {
        using var fixture = Server();
        var nova = await fixture.RegisterAndLogin("nova", playerName: "Nova");
        var rest = Rest(fixture);

        var response = await rest.PostAsJsonAsync("/api/admin/bans", new { playerId = nova.Player.PlayerId, reason = "rest ban", days = 3 });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var ex = await Assert.ThrowsAsync<RpcException>(() => new Protocol.V1.Auth.AuthClient(fixture.CreateChannel()).LoginAsync(PasswordLogin("nova", "pw")).ResponseAsync);
        Assert.Equal(ErrorCode.Banned, ex.ErrorDetail()?.Code);
        var actions = await rest.GetFromJsonAsync<JsonElement>("/api/admin/actions");
        Assert.Contains(actions.EnumerateArray(), a => a.GetProperty("action").GetString() == "ban");
        var bans = await rest.GetFromJsonAsync<JsonElement>("/api/admin/bans");
        long banId = bans.EnumerateArray().Single().GetProperty("id").GetInt64();
        Assert.Equal(HttpStatusCode.OK, (await rest.DeleteAsync($"/api/admin/bans/{banId}")).StatusCode);
        Assert.NotEmpty((await fixture.RegisterAndLogin("nova")).SessionToken);
    }

    [Fact]
    public async Task ModpackCanBeReadAndReplaced()
    {
        using var fixture = Server();
        var rest = Rest(fixture);
        var list = new { entries = new[] { new { position = 0, packageId = "Ludeon.RimWorld", version = "1.7", workshopId = 0UL, url = "", kind = "required" } } };

        Assert.Equal(HttpStatusCode.OK, (await rest.PutAsJsonAsync("/api/admin/modpack", list)).StatusCode);

        var stored = await rest.GetFromJsonAsync<JsonElement>("/api/admin/modpack");
        Assert.Equal("ludeon.rimworld", stored.GetProperty("entries").EnumerateArray().Single().GetProperty("packageId").GetString());
    }

    [Fact]
    public async Task BroadcastReachesPlayers()
    {
        using var fixture = Server();
        var nova = await fixture.RegisterAndLogin("nova");
        using var stream = new Protocol.V1.Events.EventsClient(fixture.CreateChannel()).Subscribe(new Empty(), Bearer(nova.SessionToken));

        Assert.Equal(HttpStatusCode.OK, (await Rest(fixture).PostAsJsonAsync("/api/admin/broadcast", new { text = "restart soon" })).StatusCode);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        string? seen = null;
        while (seen is null && await stream.ResponseStream.MoveNext(cts.Token))
            if (stream.ResponseStream.Current.BodyCase == ServerEvent.BodyOneofCase.Notice) seen = stream.ResponseStream.Current.Notice.Text;
        Assert.Equal("restart soon", seen);
    }
}
