using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Rimlink.Server.Rest;
using Rimlink.Server.Tests.Support;
using Xunit;

namespace Rimlink.Server.Tests.Admin;

public class AuditActorTests
{
    private const string BotToken = "bot-token-0123456789abcdefgh";

    private static ServerFixture Server() => new ServerFixture()
        .WithSetting("Auth:AllowPassword", "true")
        .WithSetting("Admin:ApiToken", AdminRestExtensions.ApiToken)
        .WithToken(0, "bot", BotToken, "moderate");

    private static async Task<string?> LastActorName(ServerFixture fixture)
    {
        var actions = await fixture.Rest().GetFromJsonAsync<JsonElement>("/api/admin/actions");
        return actions.EnumerateArray().First().GetProperty("actorName").GetString();
    }

    [Fact]
    public async Task ApiTokenActionsAreSignedApi()
    {
        using var fixture = Server();

        var response = await fixture.Rest().PostAsJsonAsync("/api/admin/broadcast", new { text = "hi" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal("api", await LastActorName(fixture));
    }

    [Fact]
    public async Task ActorHeaderSignsTheAction()
    {
        using var fixture = Server();

        await fixture.Rest(actor: "discord:Mira").PostAsJsonAsync("/api/admin/broadcast", new { text = "hi" });

        Assert.Equal("api:discord:Mira", await LastActorName(fixture));
    }

    [Fact]
    public async Task NamedTokenSignsWithItsName()
    {
        using var fixture = Server();

        Assert.Equal(HttpStatusCode.OK, (await fixture.Rest(BotToken).PostAsJsonAsync("/api/admin/broadcast", new { text = "a" })).StatusCode);
        Assert.Equal("api:bot", await LastActorName(fixture));

        await fixture.Rest(BotToken, "discord:Mod (42)").PostAsJsonAsync("/api/admin/broadcast", new { text = "b" });
        Assert.Equal("api:bot:discord:Mod (42)", await LastActorName(fixture));
    }

    [Theory]
    [InlineData(65)]
    [InlineData(-1)]
    public async Task BadActorHeaderIs400(int kind)
    {
        using var fixture = Server();
        string label = kind == 65 ? new string('x', 65) : "a\u0001";

        var response = await fixture.Rest(actor: label).GetAsync("/api/admin/players/online");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("X-Rimlink-Actor must be 1-64 characters without control characters", problem.GetProperty("detail").GetString());
    }

    [Fact]
    public void LabelRulesRefuseEmptyLongAndControlCharacters()
    {
        // The in-memory test transport drops a header with an empty value; Kestrel keeps it, and the filter then refuses it here.
        Assert.False(AdminAuth.IsValidLabel(""));
        Assert.False(AdminAuth.IsValidLabel(new string('x', 65)));
        Assert.False(AdminAuth.IsValidLabel("a\tb"));
        Assert.True(AdminAuth.IsValidLabel(new string('x', 64)));
        Assert.True(AdminAuth.IsValidLabel("discord:Mod (42)"));
    }

    [Fact]
    public async Task WrongTokenWithABadHeaderIsStill401()
    {
        using var fixture = Server();

        var response = await fixture.Rest("nope", new string('x', 65)).GetAsync("/api/admin/players/online");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public void TokenMatchingTrimsAndRefusesBlank()
    {
        Assert.True(AdminToken.Matches("t", " t\n"));
        Assert.False(AdminToken.Matches("", ""));
        Assert.False(AdminToken.Matches(null, "t"));
        Assert.False(AdminToken.Matches("t", "u"));
        Assert.False(AdminToken.Matches("t", null));
    }
}
