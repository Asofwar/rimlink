using Google.Protobuf;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Auth;
using Rimlink.Server.Storage;

namespace Rimlink.Server.Tests.Support;

/// <summary>
/// In-process server with TLS enforcement off, rate limits off, a throwaway data directory and (by default) a world
/// already created, so plain players can log in. Steam tickets are checked by <see cref="FakeSteamVerifier"/>.
/// </summary>
public class ServerFixture : WebApplicationFactory<Program>
{
    private readonly Dictionary<string, string?> settings = new() { ["RateLimit:Disabled"] = "true", ["Backup:IntervalHours"] = "0" };
    private bool seedWorld = true;

    public const string WorldSeed = "Rimlink";

    public string DataDir { get; } = Path.Combine(Path.GetTempPath(), "rimlink-tests", Guid.NewGuid().ToString("N"));

    public ServerFixture WithSetting(string key, string? value)
    {
        settings[key] = value;
        return this;
    }

    public ServerFixture WithRateLimits() => WithSetting("RateLimit:Disabled", "false");

    /// <summary>Start without a world: only admins can log in until World.Create is called.</summary>
    public ServerFixture WithoutWorld()
    {
        seedWorld = false;
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

    protected override IHost CreateHost(IHostBuilder builder)
    {
        var host = base.CreateHost(builder);
        if (seedWorld)
        {
            using var scope = host.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<RimlinkDbContext>();
            db.World.Add(new Server.Storage.Entities.World
            {
                Seed = WorldSeed,
                Params = new WorldParams { Seed = WorldSeed, PlanetCoverage = 0.3f }.ToByteArray(),
                NpcTiles = "",
                CreatedAt = DateTime.UtcNow,
            });
            db.SaveChanges();
        }
        return host;
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
