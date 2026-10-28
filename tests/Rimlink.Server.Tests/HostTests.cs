using Rimlink.Server.Tests.Support;
using Xunit;

namespace Rimlink.Server.Tests;

public class HostTests
{
    [Fact]
    public async Task HealthEndpointReturnsOk()
    {
        using var fixture = new ServerFixture();
        using var client = fixture.CreateClient();

        var response = await client.GetAsync("/health");

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Healthy", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public void StartupFailsWithoutTlsWhenRequired()
    {
        using var fixture = new ServerFixture().WithSetting("Tls:Required", "true");

        var ex = Assert.ThrowsAny<Exception>(() => fixture.CreateClient());

        string messages = string.Join(" | ", Chain(ex).Select(e => e.Message));
        Assert.Contains("TLS", messages);
    }

    private static IEnumerable<Exception> Chain(Exception ex)
    {
        for (Exception? e = ex; e != null; e = e.InnerException) yield return e;
    }
}
