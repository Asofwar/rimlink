using System.Net;
using System.Net.Http.Json;
using Xunit;

namespace Rimlink.Server.Tests.Support;

/// <summary>Panel logins for tests. TestServer speaks plain HTTP, so the Secure cookie is read from Set-Cookie and sent by header.</summary>
public static class PanelTestExtensions
{
    public const string CookieName = "rimlink_panel";

    public static HttpClient PanelAnonymous(this ServerFixture fixture)
    {
        var client = fixture.CreateClient();
        client.DefaultRequestHeaders.Add("X-Rimlink-Panel", "1");
        return client;
    }

    public static async Task<HttpResponseMessage> PanelLoginResponse(this ServerFixture fixture, string token) =>
        await fixture.PanelAnonymous().PostAsJsonAsync("/api/panel/login", new { token });

    /// <summary>Logs in and returns the session cookie's value.</summary>
    public static async Task<string> PanelLogin(this ServerFixture fixture, string token = AdminRestExtensions.ApiToken)
    {
        var response = await fixture.PanelLoginResponse(token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return CookieValue(response);
    }

    public static string CookieValue(HttpResponseMessage response)
    {
        string header = response.Headers.GetValues("Set-Cookie").Single(h => h.StartsWith(CookieName + "=", StringComparison.Ordinal));
        return header[(CookieName.Length + 1)..header.IndexOf(';')];
    }

    public static HttpClient PanelClient(this ServerFixture fixture, string cookieValue, bool panelHeader = true)
    {
        var client = fixture.CreateClient();
        client.DefaultRequestHeaders.Add("Cookie", $"{CookieName}={cookieValue}");
        if (panelHeader) client.DefaultRequestHeaders.Add("X-Rimlink-Panel", "1");
        return client;
    }
}
