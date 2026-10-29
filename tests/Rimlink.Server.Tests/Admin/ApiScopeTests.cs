using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Rimlink.Server.Application.Admin;
using Rimlink.Server.Rest;
using Rimlink.Server.Tests.Support;
using Xunit;

namespace Rimlink.Server.Tests.Admin;

public class ApiScopeTests
{
    private const string ReadToken = "read-token-0123456789abcdef";
    private const string ModToken = "mod-token-0123456789abcdefg";
    private const string ChatToken = "chat-token-0123456789abcdef";
    private const string BossToken = "boss-token-0123456789abcdef";

    private static ServerFixture Server() => new ServerFixture()
        .WithSetting("Auth:AllowPassword", "true")
        .WithSetting("Admin:ApiToken", AdminRestExtensions.ApiToken)
        .WithToken(0, "reader", ReadToken, "read")
        .WithToken(1, "mod", ModToken, "moderate")
        .WithToken(2, "chatter", ChatToken, "chat")
        .WithToken(3, "boss", BossToken, "admin");

    [Fact]
    public async Task ScopesGateRoutes()
    {
        using var fixture = Server();
        var nova = await fixture.RegisterAndLogin("nova", playerName: "Nova");
        var mira = await fixture.RegisterAndLogin("mira", playerName: "Mira");

        var read = fixture.Rest(ReadToken);
        Assert.Equal(HttpStatusCode.OK, (await read.GetAsync("/api/admin/modpack")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await read.GetAsync("/api/admin/players/online")).StatusCode);
        var refused = await read.PostAsJsonAsync("/api/admin/broadcast", new { text = "x" });
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal("this needs the moderate scope", (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString());

        var mod = fixture.Rest(ModToken);
        Assert.Equal(HttpStatusCode.OK, (await mod.GetAsync("/api/admin/players/online")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await mod.PostAsJsonAsync($"/api/admin/players/{nova.Player.PlayerId}/kick", new { reason = "test" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await mod.PostAsJsonAsync("/api/admin/bans", new { playerId = mira.Player.PlayerId, reason = "x" })).StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden, (await fixture.Rest(ChatToken).GetAsync("/api/admin/modpack")).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await fixture.Rest(BossToken).PostAsJsonAsync("/api/admin/bans", new { playerId = mira.Player.PlayerId, reason = "x" })).StatusCode);

        var api = fixture.Rest();
        Assert.Equal(HttpStatusCode.OK, (await api.GetAsync("/api/admin/players/online")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await api.PostAsJsonAsync("/api/admin/broadcast", new { text = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await api.GetAsync("/api/admin/bans")).StatusCode);
    }

    [Fact]
    public void ScopeExpansion()
    {
        Assert.True(ApiScopes.Grants(ApiScope.Admin, ApiScope.Read | ApiScope.Chat | ApiScope.Moderate));
        Assert.True(ApiScopes.Grants(ApiScope.Moderate, ApiScope.Read));
        Assert.False(ApiScopes.Grants(ApiScope.Moderate, ApiScope.Admin));
        Assert.False(ApiScopes.Grants(ApiScope.Chat, ApiScope.Read));
        Assert.False(ApiScopes.Grants(ApiScope.Read, ApiScope.Chat));
    }

    [Theory]
    [InlineData("Bot!", "bot-token-0123456789abcdefgh", "read", "Admin:Tokens[0]")]
    [InlineData("bot", "short-token-0123456789a", "read", "Admin:Tokens[0]")] // 23 characters
    [InlineData("bot", "bot-token-0123456789abcdefgh", "", "Admin:Tokens[0]")] // no scopes
    [InlineData("bot", "bot-token-0123456789abcdefgh", "owner", "Admin:Tokens[0]")]
    public void BadTokenConfigStopsTheStart(string name, string token, string scope, string entry)
    {
        using var fixture = new ServerFixture().WithSetting("Admin:ApiToken", AdminRestExtensions.ApiToken);
        if (scope.Length > 0) fixture.WithToken(0, name, token, scope); else fixture.WithToken(0, name, token);

        var ex = Assert.ThrowsAny<Exception>(() => fixture.CreateClient());

        Assert.Contains(entry, ex.ToString());
        Assert.DoesNotContain(token, ex.ToString());
    }

    [Fact]
    public void DuplicateNamesAndReusedTokensStopTheStart()
    {
        using var duplicate = new ServerFixture()
            .WithToken(0, "bot", "bot-token-0123456789abcdefgh", "read")
            .WithToken(1, "bot", "bot-token-0123456789abcdefgX", "read");
        Assert.Contains("Admin:Tokens[1]", Assert.ThrowsAny<Exception>(() => duplicate.CreateClient()).ToString());

        using var reused = new ServerFixture()
            .WithSetting("Admin:ApiToken", "same-token-0123456789abcdef")
            .WithToken(0, "bot", "same-token-0123456789abcdef", "read");
        Assert.Contains("Admin:Tokens[0]", Assert.ThrowsAny<Exception>(() => reused.CreateClient()).ToString());
    }

    [Fact]
    public async Task RestIsOnWithOnlyNamedTokens()
    {
        using var fixture = new ServerFixture().WithSetting("Admin:ApiToken", "").WithToken(0, "reader", ReadToken, "read");

        Assert.Equal(HttpStatusCode.OK, (await fixture.Rest(ReadToken).GetAsync("/api/admin/modpack")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await fixture.Rest("").GetAsync("/api/admin/modpack")).StatusCode);
    }

    [Fact]
    public void EveryAdminRouteDeclaresAScope()
    {
        using var fixture = Server();
        fixture.CreateClient();

        var endpoints = fixture.Services.GetServices<EndpointDataSource>().SelectMany(s => s.Endpoints).OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText?.TrimStart('/').StartsWith("api/admin", StringComparison.Ordinal) == true)
            .ToList();

        Assert.NotEmpty(endpoints);
        Assert.All(endpoints, e => Assert.True(e.Metadata.GetMetadata<RequiredScope>() is not null, $"{e.RoutePattern.RawText} has no scope"));
    }
}
