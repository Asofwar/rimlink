using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Storage.Entities;
using Rimlink.Server.Tests.Support;
using Xunit;

namespace Rimlink.Server.Tests.Outposts;

public sealed class OutpostStartupTests
{
    [Fact]
    public void Legacy_empty_server_does_not_bootstrap_or_enable_outposts()
    {
        using var server = new ServerFixture();
        using var client = server.CreateClient();
        var feature = server.Services.GetRequiredService<OutpostFeature>();
        Assert.Equal(OutpostStartupState.RequiresInitialization, feature.Startup.State);
        Assert.False(feature.IsAvailable);
        Assert.False(File.Exists(Path.Combine(server.DataDir, "authority.db")));
    }

    [Fact]
    public void Retained_history_without_anchor_stops_before_any_hosted_service_even_when_disabled()
    {
        using var h = new AuthorityHarness();
        h.Db.Outposts.Add(new OutpostRow { Id = "retained-history", State = "Closed" });
        h.Db.SaveChanges();
        var probe = new StartupProbe();
        using var server = new ServerFixture().WithDataDir(h.Data.Root).KeepDataDir().WithoutWorld()
            .WithServices(services => services.AddSingleton<IHostedService>(probe));

        var failure = Assert.ThrowsAny<Exception>(() => server.CreateClient());

        Assert.Contains("outpost-startup-blocked", failure.ToString());
        Assert.False(probe.Started);
        Assert.Equal("Closed", h.Db.Outposts.Single().State);
        server.Dispose();
        using var released = new FileStream(Path.Combine(h.Data.Root, "server.lock"),
            FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Fact]
    public void Process_lock_is_required_before_migrations_create_the_database()
    {
        using var server = new ServerFixture();
        Directory.CreateDirectory(server.DataDir);
        using var blocker = new FileStream(Path.Combine(server.DataDir, "server.lock"),
            FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        Assert.ThrowsAny<Exception>(() => server.CreateClient());
        Assert.False(File.Exists(Path.Combine(server.DataDir, "rimlink.db")));
    }

    private sealed class StartupProbe : IHostedService
    {
        public bool Started { get; private set; }
        public Task StartAsync(CancellationToken ct) { Started = true; return Task.CompletedTask; }
        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
    }
}
