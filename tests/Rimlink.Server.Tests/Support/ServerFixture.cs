using Google.Protobuf;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
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
    private readonly List<Action<IServiceCollection>> serviceOverrides = [];
    private bool seedWorld = true;
    private string? connectionString;
    private bool keepDataDir;

    public const string WorldSeed = "Rimlink";

    public string DataDir { get; private set; } = Path.Combine(Path.GetTempPath(), "rimlink-tests", Guid.NewGuid().ToString("N"));

    public ServerFixture WithSetting(string key, string? value)
    {
        settings[key] = value;
        return this;
    }

    /// <summary>Leave the data directory on disk when disposed, so a later fixture can restart over it (<see cref="WithDataDir"/>).</summary>
    public ServerFixture KeepDataDir()
    {
        keepDataDir = true;
        return this;
    }

    /// <summary>Start over an earlier fixture's data directory, as a server restart does; this fixture deletes it when disposed.</summary>
    public ServerFixture WithDataDir(string dataDir)
    {
        DataDir = dataDir;
        return this;
    }

    public ServerFixture WithRateLimits() => WithSetting("RateLimit:Disabled", "false");

    /// <summary>Replace or add services after the fixture's own test replacements (e.g. a manual <see cref="TimeProvider"/>).</summary>
    public ServerFixture WithServices(Action<IServiceCollection> configure)
    {
        serviceOverrides.Add(configure);
        return this;
    }

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
            foreach (var configure in serviceOverrides) configure(services);
        });
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        var host = base.CreateHost(builder);
        using (var scope = host.Services.CreateScope())
            connectionString = scope.ServiceProvider.GetRequiredService<RimlinkDbContext>().Database.GetConnectionString();
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
            // Pooled connections outlive the host and keep the database open: Windows then refuses the delete, Linux deletes it anyway.
            if (connectionString is not null) SqliteConnection.ClearPool(new SqliteConnection(connectionString));
            if (!keepDataDir)
            {
                try { Directory.Delete(DataDir, recursive: true); } catch (IOException) { }
            }
        }
    }
}
