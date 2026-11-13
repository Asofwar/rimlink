using System.Diagnostics;
using Google.Protobuf;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
    private ProviderDisposalCompletion? providerDisposal;
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
        builder.UseSetting("Outposts:QualifiedLinuxFileSystem", LocalTestStorage.LinuxQualification(DataDir));
        foreach (var (key, value) in settings) builder.UseSetting(key, value);
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<ISteamTicketVerifier, FakeSteamVerifier>();
            services.AddTransient<IStartupFilter, LoopbackIpStartupFilter>();
            foreach (var configure in serviceOverrides) configure(services);
            var logging = services.Last(d => d.ServiceType == typeof(ILoggerFactory) && !d.IsKeyedService);
            services.Remove(logging);
            services.AddSingleton(_ => providerDisposal = new ProviderDisposalCompletion());
            services.AddSingleton<ILoggerFactory>(provider =>
            {
                // Construct before logging/storage so the provider signals only after those resources have closed.
                _ = provider.GetRequiredService<ProviderDisposalCompletion>();
                return (ILoggerFactory)(logging.ImplementationFactory ?? throw new InvalidOperationException("test logger factory ownership is unknown"))(provider);
            });
        });
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        // Startup may throw before a host exists; still clear this exact owned pool on disposal.
        connectionString = $"Data Source={Path.Combine(DataDir, "rimlink.db")};Pooling=True";
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

    /// <summary>Minimal-host test startup can race host disposal; preserve the durable initialization diagnostic too.</summary>
    public string StartupFailureDiagnostic(Exception error) => error + "\n" + ReadStartupLog();

    public string ReadStartupLog()
    {
        string logs = Path.Combine(DataDir, "logs");
        if (!Directory.Exists(logs)) return "";
        return string.Join("\n", Directory.GetFiles(logs, "server-*.log").Select(path =>
        {
            // A failed startup can still be closing its writer when the test reads its original diagnostic.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }));
    }

    public GrpcChannel CreateChannel() =>
        GrpcChannel.ForAddress(Server.BaseAddress, new GrpcChannelOptions { HttpClient = CreateDefaultClient() });

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            // The entry point may already be disposing this provider; a second Dispose can otherwise return early.
            providerDisposal?.Finished.Task.GetAwaiter().GetResult();
            // Pooled connections outlive the host and keep the database open: Windows then refuses the delete, Linux deletes it anyway.
            if (connectionString is not null) SqliteConnection.ClearPool(new SqliteConnection(connectionString));
            if (!keepDataDir) DeleteDataDir();
        }
    }

    private void DeleteDataDir()
    {
        // A request or background loop still finishing can return its connection to the pool after the clear above, which
        // reopens the hold on rimlink.db; clear again until the delete goes through (measured: under a second when it happens).
        var waited = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                if (Directory.Exists(DataDir)) Directory.Delete(DataDir, recursive: true);
                return;
            }
            catch (IOException) when (waited.Elapsed < TimeSpan.FromSeconds(5))
            {
                Thread.Sleep(50);
                if (connectionString is not null) SqliteConnection.ClearPool(new SqliteConnection(connectionString));
            }
            catch (IOException) { return; }
        }
    }

    private sealed class ProviderDisposalCompletion : IDisposable
    {
        internal TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Dispose() => Finished.TrySetResult();
    }
}
