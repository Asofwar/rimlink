using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Rimlink.Server.Tests.Support;
using Xunit;

namespace Rimlink.Server.Tests.Panel;

public class PanelAuthTests
{
    private const string BossToken = "boss-token-0123456789abcdef";
    private readonly ManualTimeProvider clock = new();

    private ServerFixture Server() => new ServerFixture()
        .WithSetting("Auth:AllowPassword", "true")
        .WithSetting("Admin:ApiToken", AdminRestExtensions.ApiToken)
        .WithServices(s => s.AddSingleton<TimeProvider>(clock));

    private static async Task<List<string?>> Actions(ServerFixture fixture) =>
        (await fixture.Rest().GetFromJsonAsync<JsonElement>("/api/admin/actions")).EnumerateArray().Select(a => a.GetProperty("action").GetString()).ToList();

    [Fact]
    public async Task LoginSetsAStrictHttpOnlySecureCookie()
    {
        using var fixture = Server();

        var response = await fixture.PanelLoginResponse(AdminRestExtensions.ApiToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        string cookie = response.Headers.GetValues("Set-Cookie").Single().ToLowerInvariant();
        Assert.StartsWith("rimlink_panel=", cookie);
        foreach (string flag in new[] { "httponly", "secure", "samesite=strict", "path=/" }) Assert.Contains(flag, cookie);
        Assert.True(response.Headers.CacheControl?.NoStore);
    }

    [Fact]
    public async Task CookieSessionCanCallTheAdminApi()
    {
        using var fixture = Server();
        string cookie = await fixture.PanelLogin();

        var response = await fixture.PanelClient(cookie).GetAsync("/api/admin/players/online");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
    }

    [Fact]
    public async Task LoginTrimsTheToken()
    {
        using var fixture = Server();

        Assert.Equal(HttpStatusCode.OK, (await fixture.PanelLoginResponse(" " + AdminRestExtensions.ApiToken + "\n")).StatusCode);
    }

    [Fact]
    public async Task WrongTokenIs401AndTheSixthAttemptIs429()
    {
        using var fixture = Server();

        for (int i = 0; i < 5; i++) Assert.Equal(HttpStatusCode.Unauthorized, (await fixture.PanelLoginResponse("wrong")).StatusCode);
        var locked = await fixture.PanelLoginResponse(AdminRestExtensions.ApiToken);

        Assert.Equal(HttpStatusCode.TooManyRequests, locked.StatusCode);
        Assert.NotNull(locked.Headers.RetryAfter?.Delta);
    }

    [Fact]
    public async Task LockoutEndsAfterTheWindow()
    {
        using var fixture = Server();
        for (int i = 0; i < 5; i++) await fixture.PanelLoginResponse("wrong");

        clock.Advance(TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(1));

        Assert.Equal(HttpStatusCode.OK, (await fixture.PanelLoginResponse(AdminRestExtensions.ApiToken)).StatusCode);
    }

    [Fact]
    public async Task SuccessDoesNotResetTheFailureCount()
    {
        using var fixture = Server();

        for (int i = 0; i < 4; i++) await fixture.PanelLoginResponse("wrong");
        Assert.Equal(HttpStatusCode.OK, (await fixture.PanelLoginResponse(AdminRestExtensions.ApiToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await fixture.PanelLoginResponse("wrong")).StatusCode);

        Assert.Equal(HttpStatusCode.TooManyRequests, (await fixture.PanelLoginResponse(AdminRestExtensions.ApiToken)).StatusCode);
    }

    [Fact]
    public async Task LogoutEndsTheSession()
    {
        using var fixture = Server();
        string cookie = await fixture.PanelLogin();
        var panel = fixture.PanelClient(cookie);

        Assert.Equal(HttpStatusCode.OK, (await panel.PostAsync("/api/panel/logout", null)).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await panel.GetAsync("/api/admin/players/online")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await panel.GetAsync("/api/panel/session")).StatusCode);
    }

    [Fact]
    public async Task SessionExpiresAfterSessionHours()
    {
        using var fixture = Server();
        string cookie = await fixture.PanelLogin();
        Assert.Equal(HttpStatusCode.OK, (await fixture.PanelClient(cookie).GetAsync("/api/panel/session")).StatusCode);

        clock.Advance(TimeSpan.FromHours(12) + TimeSpan.FromSeconds(1));

        Assert.Equal(HttpStatusCode.Unauthorized, (await fixture.PanelClient(cookie).GetAsync("/api/panel/session")).StatusCode);
    }

    [Fact]
    public async Task TwentyFirstLoginEvictsTheOldest()
    {
        using var fixture = Server();
        var cookies = new List<string>();
        for (int i = 0; i < 21; i++)
        {
            cookies.Add(await fixture.PanelLogin());
            clock.Advance(TimeSpan.FromSeconds(1));
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await fixture.PanelClient(cookies[0]).GetAsync("/api/panel/session")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await fixture.PanelClient(cookies[20]).GetAsync("/api/panel/session")).StatusCode);
    }

    [Fact]
    public async Task CookiePostWithoutThePanelHeaderIs403()
    {
        using var fixture = Server();
        string cookie = await fixture.PanelLogin();

        var response = await fixture.PanelClient(cookie, panelHeader: false).PostAsJsonAsync("/api/admin/broadcast", new { text = "x" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.DoesNotContain("broadcast", await Actions(fixture));
    }

    [Fact]
    public async Task CookiePostFromAForeignOriginIs403()
    {
        using var fixture = Server();
        var panel = fixture.PanelClient(await fixture.PanelLogin());
        panel.DefaultRequestHeaders.Add("Origin", "https://evil.example");

        Assert.Equal(HttpStatusCode.Forbidden, (await panel.PostAsJsonAsync("/api/admin/broadcast", new { text = "x" })).StatusCode);
    }

    [Fact]
    public async Task CookiePostWithTheSameOriginPasses()
    {
        using var fixture = Server();
        var panel = fixture.PanelClient(await fixture.PanelLogin());
        panel.DefaultRequestHeaders.Add("Origin", "http://localhost");

        Assert.Equal(HttpStatusCode.OK, (await panel.PostAsJsonAsync("/api/admin/broadcast", new { text = "x" })).StatusCode);
    }

    [Fact]
    public async Task LoginWithoutThePanelHeaderIs403()
    {
        using var fixture = Server();

        var response = await fixture.CreateClient().PostAsJsonAsync("/api/panel/login", new { token = AdminRestExtensions.ApiToken });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task BearerNeedsNoPanelHeader()
    {
        using var fixture = Server();
        var api = fixture.Rest();
        api.DefaultRequestHeaders.Add("Origin", "https://evil.example");

        Assert.Equal(HttpStatusCode.OK, (await api.PostAsJsonAsync("/api/admin/broadcast", new { text = "x" })).StatusCode);
    }

    [Fact]
    public async Task CookieActionsAreSignedPanel()
    {
        using var fixture = Server();

        await fixture.PanelClient(await fixture.PanelLogin()).PostAsJsonAsync("/api/admin/broadcast", new { text = "x" });

        var actions = await fixture.Rest().GetFromJsonAsync<JsonElement>("/api/admin/actions");
        Assert.Equal("panel", actions.EnumerateArray().First().GetProperty("actorName").GetString());
    }

    [Fact]
    public async Task PanelApiIsOffWithoutToken()
    {
        using var fixture = new ServerFixture().WithSetting("Admin:ApiToken", "").WithToken(0, "boss", BossToken, "admin");

        Assert.Equal(HttpStatusCode.NotFound, (await fixture.PanelLoginResponse(BossToken)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await fixture.PanelAnonymous().GetAsync("/api/panel/session")).StatusCode);
    }

    [Fact]
    public async Task NamedTokenCannotLogIn()
    {
        using var fixture = Server().WithToken(0, "boss", BossToken, "admin");

        Assert.Equal(HttpStatusCode.Unauthorized, (await fixture.PanelLoginResponse(BossToken)).StatusCode);
    }
}
