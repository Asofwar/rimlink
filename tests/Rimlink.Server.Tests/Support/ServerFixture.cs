using Grpc.Net.Client;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Rimlink.Server.Application.Auth;

namespace Rimlink.Server.Tests.Support;

/// <summary>In-process server with TLS enforcement off and a throwaway data directory.</summary>
public class ServerFixture : WebApplicationFactory<Program>
{
    private readonly Dictionary<string, string?> settings = new();

    public string DataDir { get; } = Path.Combine(Path.GetTempPath(), "rimlink-tests", Guid.NewGuid().ToString("N"));

    public ServerFixture WithSetting(string key, string? value)
    {
        settings[key] = value;
        return this;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        Directory.CreateDirectory(DataDir);
        builder.UseSetting("Tls:Required", "false");
        builder.UseSetting("Server:DataDir", DataDir);
        foreach (var (key, value) in settings) builder.UseSetting(key, value);
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<ISteamTicketVerifier, FakeSteamVerifier>();
            services.AddTransient<IStartupFilter, LoopbackIpStartupFilter>();
        });
    }

    public GrpcChannel CreateChannel() =>
        GrpcChannel.ForAddress(Server.BaseAddress, new GrpcChannelOptions { HttpClient = CreateDefaultClient() });

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            try { Directory.Delete(DataDir, recursive: true); } catch (IOException) { }
        }
    }
}
