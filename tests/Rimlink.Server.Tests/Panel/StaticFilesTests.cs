using System.Net;
using Rimlink.Server.Tests.Support;
using Xunit;

namespace Rimlink.Server.Tests.Panel;

public class StaticFilesTests
{
    private const string Csp = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";

    private static ServerFixture Server() => new ServerFixture()
        .WithSetting("Auth:AllowPassword", "true")
        .WithSetting("Admin:ApiToken", AdminRestExtensions.ApiToken);

    [Theory]
    [InlineData("/")]
    [InlineData("/admin/")]
    public async Task PublicPageAndAdminAreServedWithCsp(string path)
    {
        using var fixture = Server();

        var response = await fixture.CreateClient().GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(Csp, string.Join("", response.Headers.GetValues("Content-Security-Policy")));
        Assert.Equal("nosniff", string.Join("", response.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal("no-referrer", string.Join("", response.Headers.GetValues("Referrer-Policy")));
    }

    [Fact]
    public async Task AdminWithoutTrailingSlashRedirects()
    {
        using var fixture = Server();
        var client = fixture.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync("/admin");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/admin/", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task AdminIsHiddenWithoutToken()
    {
        using var fixture = new ServerFixture().WithSetting("Admin:ApiToken", "");
        var client = fixture.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/admin/")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/admin/app.js")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/")).StatusCode);
    }

    [Fact]
    public async Task PublicPageCanBeTurnedOff()
    {
        using var fixture = Server().WithSetting("Panel:Public", "false");
        var client = fixture.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/public.js")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/admin/")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/style.css")).StatusCode);
    }

    [Fact]
    public async Task PanelOffHidesEverything()
    {
        using var fixture = Server().WithSetting("Panel:Enabled", "false");
        var client = fixture.CreateClient();

        foreach (string path in new[] { "/", "/admin/", "/style.css", "/api/public/status", "/api/panel/session" })
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(path)).StatusCode);
    }

    [Fact]
    public async Task GrpcStillWorks()
    {
        using var fixture = Server();

        Assert.NotEmpty((await fixture.RegisterAndLogin("nova")).SessionToken);
    }
}
