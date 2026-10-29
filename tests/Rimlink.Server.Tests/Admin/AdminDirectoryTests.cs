using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Rimlink.Protocol.V1;
using Rimlink.Server.Storage;
using Rimlink.Server.Tests.Support;
using Xunit;
using static Rimlink.Server.Tests.Support.GrpcTestExtensions;

namespace Rimlink.Server.Tests.Admin;

public class AdminDirectoryTests
{
    private const string PubToken = "pub-token-0123456789abcdefg";
    private const string ModToken = "mod-token-0123456789abcdefg";
    private const long OrinSteamId = 76561198000000001;

    private static ServerFixture Server() => new ServerFixture()
        .WithSetting("Auth:AllowPassword", "true")
        .WithSetting("Admin:ApiToken", AdminRestExtensions.ApiToken)
        .WithToken(0, "pub", PubToken, "read")
        .WithToken(1, "mod", ModToken, "moderate");

    private static async Task<JsonElement> Get(HttpClient client, string path)
    {
        var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static List<string?> Names(JsonElement page) => page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("name").GetString()).ToList();

    private static async Task SetSteamId(ServerFixture fixture, string login, long steamId)
    {
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RimlinkDbContext>();
        var account = await db.Accounts.SingleAsync(a => a.Login == login);
        account.SteamId = steamId;
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task SearchFindsByNameFactionSteamIdLoginAndId()
    {
        using var fixture = Server();
        await fixture.RegisterAndLogin("nova", playerName: "Nova");
        await fixture.RegisterAndLogin("miralogin", playerName: "Mira");
        var orin = await fixture.RegisterAndLogin("orin", playerName: "Orin");
        await SetSteamId(fixture, "orin", OrinSteamId);
        var rest = fixture.Rest();

        Assert.Equal(["Nova"], Names(await Get(rest, "/api/admin/players?q=nov")));
        Assert.Equal(["Mira"], Names(await Get(rest, "/api/admin/players?q=miralogin")));
        Assert.Equal(["Orin"], Names(await Get(rest, $"/api/admin/players?q=%23{orin.Player.PlayerId}")));
        Assert.Equal(["Orin"], Names(await Get(rest, $"/api/admin/players?q={OrinSteamId}")));
    }

    [Fact]
    public async Task SearchPutsOnlinePlayersFirstAndPages()
    {
        using var fixture = Server();
        await fixture.RegisterAndLogin("nova", playerName: "Nova");
        var mira = await fixture.RegisterAndLogin("mira", playerName: "Mira");
        var orin = await fixture.RegisterAndLogin("orin", playerName: "Orin");
        var rest = fixture.Rest();
        await rest.PostAsJsonAsync($"/api/admin/players/{mira.Player.PlayerId}/kick", new { reason = "x" });
        await rest.PostAsJsonAsync($"/api/admin/players/{orin.Player.PlayerId}/kick", new { reason = "x" });

        var all = await Get(rest, "/api/admin/players");
        Assert.Equal("Nova", Names(all)[0]);
        Assert.True(all.GetProperty("items")[0].GetProperty("online").GetBoolean());
        Assert.False(all.GetProperty("items")[1].GetProperty("online").GetBoolean());

        var page = await Get(rest, "/api/admin/players?limit=1&offset=1");
        Assert.Equal(3, page.GetProperty("total").GetInt32());
        Assert.Single(Names(page));
    }

    [Fact]
    public async Task SearchPutsTheExactNameFirst()
    {
        using var fixture = Server();
        await fixture.RegisterAndLogin("novak", playerName: "Novak");
        var nova = await fixture.RegisterAndLogin("nova", playerName: "Nova");
        var rest = fixture.Rest();
        await rest.PostAsJsonAsync($"/api/admin/players/{nova.Player.PlayerId}/kick", new { reason = "x" });

        Assert.Equal(["Nova", "Novak"], Names(await Get(rest, "/api/admin/players?q=nova")));
    }

    [Theory]
    [InlineData("limit=0")]
    [InlineData("limit=201")]
    [InlineData("offset=-1")]
    public async Task SearchRejectsOutOfRangeLimits(string query)
    {
        using var fixture = Server();

        Assert.Equal(HttpStatusCode.BadRequest, (await fixture.Rest().GetAsync($"/api/admin/players?{query}")).StatusCode);
    }

    [Fact]
    public async Task ReadTokenSeesOnlyPublicPlayerFields()
    {
        using var fixture = Server();
        await fixture.RegisterAndLogin("nova", playerName: "Nova");
        await fixture.RegisterAndLogin("miralogin", playerName: "Mira");
        await fixture.RegisterAndLogin("orin", playerName: "Orin");
        await SetSteamId(fixture, "orin", OrinSteamId);
        var pub = fixture.Rest(PubToken);

        var item = (await Get(pub, "/api/admin/players?q=nov")).GetProperty("items").EnumerateArray().Single();
        Assert.Equal(["factionName", "id", "name", "online", "role"], item.EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal(0, (await Get(pub, "/api/admin/players?q=miralogin")).GetProperty("total").GetInt32());
        Assert.Equal(0, (await Get(pub, $"/api/admin/players?q={OrinSteamId}")).GetProperty("total").GetInt32());
        Assert.Equal(["Orin"], Names(await Get(fixture.Rest(ModToken), $"/api/admin/players?q={OrinSteamId}")));
    }

    [Fact]
    public async Task CardShowsSessionWalletAndSave()
    {
        using var fixture = Server();
        var nova = await fixture.RegisterAndLogin("nova", playerName: "Nova");
        var rest = fixture.Rest();
        long id = nova.Player.PlayerId;
        Assert.Equal(HttpStatusCode.OK, (await rest.PostAsJsonAsync($"/api/admin/economy/wallet/{id}", new { delta = 50 })).StatusCode);

        var card = await Get(rest, $"/api/admin/players/{id}");

        Assert.Equal("Nova", card.GetProperty("name").GetString());
        Assert.Equal(50, card.GetProperty("walletBalance").GetInt64());
        Assert.True(card.GetProperty("online").GetBoolean());
        Assert.Equal(JsonValueKind.Object, card.GetProperty("session").ValueKind);
        Assert.Equal(JsonValueKind.Null, card.GetProperty("currentSave").ValueKind);
        Assert.Equal(50, (await Get(rest, $"/api/admin/economy/wallet/{id}")).GetProperty("balance").GetInt64());
    }

    [Fact]
    public async Task CardShowsActiveBans()
    {
        using var fixture = Server();
        var mira = await fixture.RegisterAndLogin("mira", playerName: "Mira");
        var rest = fixture.Rest();
        await rest.PostAsJsonAsync("/api/admin/bans", new { playerId = mira.Player.PlayerId, reason = "grief", days = 3 });

        var card = await Get(rest, $"/api/admin/players/{mira.Player.PlayerId}");

        Assert.True(card.GetProperty("banned").GetBoolean());
        Assert.Equal("grief", card.GetProperty("activeBans").EnumerateArray().Single().GetProperty("reason").GetString());
    }

    [Fact]
    public async Task UnknownPlayerIs404()
    {
        using var fixture = Server();

        Assert.Equal(HttpStatusCode.NotFound, (await fixture.Rest().GetAsync("/api/admin/players/999999")).StatusCode);
    }

    [Fact]
    public async Task ChatShowsOnlyThePublicChannel()
    {
        using var fixture = Server();
        var nova = await fixture.RegisterAndLogin("nova", playerName: "Nova");
        var mira = await fixture.RegisterAndLogin("mira", playerName: "Mira");
        var chat = new Protocol.V1.Chat.ChatClient(fixture.CreateChannel());
        await chat.SendAsync(new SendRequest { Text = "hello" }, Bearer(nova.SessionToken));
        await chat.SendAsync(new SendRequest { Text = "secret", ToPlayerId = mira.Player.PlayerId }, Bearer(nova.SessionToken));
        var rest = fixture.Rest();

        var texts = (await Get(rest, "/api/admin/chat")).EnumerateArray().Select(l => l.GetProperty("text").GetString()).ToList();
        Assert.Contains("hello", texts);
        Assert.DoesNotContain("secret", texts);
        Assert.DoesNotContain((await Get(rest, $"/api/admin/chat?playerId={mira.Player.PlayerId}")).EnumerateArray(), l => l.GetProperty("fromName").GetString() == "Nova");
        Assert.Equal(HttpStatusCode.BadRequest, (await rest.GetAsync("/api/admin/chat?limit=501")).StatusCode);
    }

    [Fact]
    public async Task ChatPagesBothWays()
    {
        using var fixture = Server();
        var nova = await fixture.RegisterAndLogin("nova", playerName: "Nova");
        var chat = new Protocol.V1.Chat.ChatClient(fixture.CreateChannel());
        foreach (string text in new[] { "a", "b", "c" }) await chat.SendAsync(new SendRequest { Text = text }, Bearer(nova.SessionToken));
        var rest = fixture.Rest();
        static List<string?> Texts(JsonElement lines) => lines.EnumerateArray().Select(l => l.GetProperty("text").GetString()).Where(t => t is "a" or "b" or "c").ToList();
        var newest = await Get(rest, "/api/admin/chat");
        long idOf(string t) => newest.EnumerateArray().First(l => l.GetProperty("text").GetString() == t).GetProperty("id").GetInt64();

        Assert.Equal(["c", "b", "a"], Texts(newest));
        Assert.Equal(["b", "a"], Texts(await Get(rest, $"/api/admin/chat?before={idOf("c")}")));
        Assert.Equal(["b", "c"], Texts(await Get(rest, $"/api/admin/chat?after={idOf("a")}")));
        Assert.Equal(HttpStatusCode.BadRequest, (await rest.GetAsync("/api/admin/chat?before=1&after=1")).StatusCode);
    }

    [Fact]
    public async Task RoleAndRenameWork()
    {
        using var fixture = Server();
        var nova = await fixture.RegisterAndLogin("nova", playerName: "Nova");
        await fixture.RegisterAndLogin("mira", playerName: "Mira");
        var rest = fixture.Rest();
        long id = nova.Player.PlayerId;

        Assert.Equal(HttpStatusCode.OK, (await rest.PostAsJsonAsync($"/api/admin/players/{id}/role", new { role = "moderator" })).StatusCode);
        Assert.Equal("moderator", (await Get(rest, $"/api/admin/players/{id}")).GetProperty("role").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await rest.PostAsJsonAsync($"/api/admin/players/{id}/role", new { role = "king" })).StatusCode);

        Assert.Equal(HttpStatusCode.Conflict, (await rest.PostAsJsonAsync($"/api/admin/players/{id}/rename", new { name = "Mira" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await rest.PostAsJsonAsync($"/api/admin/players/{id}/rename", new { name = "" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await rest.PostAsJsonAsync($"/api/admin/players/{id}/rename", new { name = "Nova2" })).StatusCode);
        Assert.Equal("Nova2", (await Get(rest, $"/api/admin/players/{id}")).GetProperty("name").GetString());
    }

    [Fact]
    public async Task SaveAllIsLogged()
    {
        using var fixture = Server();
        var rest = fixture.Rest();

        Assert.Equal(HttpStatusCode.OK, (await rest.PostAsync("/api/admin/saveall", null)).StatusCode);

        Assert.Contains((await Get(rest, "/api/admin/actions")).EnumerateArray(), a => a.GetProperty("action").GetString() == "saveall");
    }

    [Fact]
    public async Task ActionsCarryTargetNameAndPageBackwards()
    {
        using var fixture = Server();
        var nova = await fixture.RegisterAndLogin("nova", playerName: "Nova");
        var rest = fixture.Rest();
        await rest.PostAsync("/api/admin/saveall", null);
        await rest.PostAsJsonAsync($"/api/admin/players/{nova.Player.PlayerId}/kick", new { reason = "x" });

        var first = (await Get(rest, "/api/admin/actions")).EnumerateArray().First();
        Assert.Equal("Nova", first.GetProperty("targetName").GetString());
        long newest = first.GetProperty("id").GetInt64();

        var older = (await Get(rest, $"/api/admin/actions?before={newest}")).EnumerateArray().ToList();
        Assert.NotEmpty(older);
        Assert.DoesNotContain(older, a => a.GetProperty("id").GetInt64() >= newest);
    }

    [Fact]
    public async Task BansCarryThePlayerName()
    {
        using var fixture = Server();
        var mira = await fixture.RegisterAndLogin("mira", playerName: "Mira");
        var rest = fixture.Rest();
        await rest.PostAsJsonAsync("/api/admin/bans", new { playerId = mira.Player.PlayerId, reason = "x" });
        await rest.PostAsJsonAsync("/api/admin/bans", new { ip = "10.0.0.9", reason = "y" });

        var bans = (await Get(rest, "/api/admin/bans")).EnumerateArray().ToList();

        Assert.Equal("Mira", bans.Single(b => b.GetProperty("reason").GetString() == "x").GetProperty("playerName").GetString());
        Assert.Equal(JsonValueKind.Null, bans.Single(b => b.GetProperty("reason").GetString() == "y").GetProperty("playerName").ValueKind);
    }
}
