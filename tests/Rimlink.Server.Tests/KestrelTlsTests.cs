using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Rimlink.Server.Hosting;
using Xunit;

namespace Rimlink.Server.Tests;

/// <summary>Real Kestrel on a random port with the generated self-signed certificate, called the way the game client will.</summary>
public class KestrelTlsTests
{
    [Fact]
    public async Task SelfSignedHttp2EndpointServesHealth()
    {
        string dataDir = Path.Combine(Path.GetTempPath(), "rimlink-tests", Guid.NewGuid().ToString("N"));
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(k => KestrelSetup.Configure(k, new ServerOptions { Port = 0, DataDir = dataDir }, new TlsOptions()));
        builder.Services.AddHealthChecks();
        await using var app = builder.Build();
        app.MapHealthChecks("/health");
        await app.StartAsync();
        try
        {
            string address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            Assert.StartsWith("https://", address);
            int port = new Uri(address).Port;

            using var handler = new SocketsHttpHandler { SslOptions = { RemoteCertificateValidationCallback = (_, _, _, _) => true } };
            using var client = new HttpClient(handler) { DefaultRequestVersion = HttpVersion.Version20, DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact };
            var response = await client.GetAsync($"https://127.0.0.1:{port}/health");

            Assert.Equal(HttpVersion.Version20, response.Version);
            Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
            Assert.True(File.Exists(Path.Combine(dataDir, "tls", "server.pfx")));

            // The admin CLI, curl and browsers speak HTTP/1.1; the same port must serve them (ALPN picks h2 for gRPC).
            using var legacy = new HttpClient(handler) { DefaultRequestVersion = HttpVersion.Version11, DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact };
            var legacyResponse = await legacy.GetAsync($"https://127.0.0.1:{port}/health");
            Assert.Equal(HttpVersion.Version11, legacyResponse.Version);
            Assert.Equal("Healthy", await legacyResponse.Content.ReadAsStringAsync());
        }
        finally
        {
            await app.StopAsync();
            Directory.Delete(dataDir, recursive: true);
        }
    }
}
