using Microsoft.EntityFrameworkCore;
using Rimlink.Server.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Rimlink.Server.Tests.Outposts;

public sealed class OutpostStartupDisposalTests
{
    [Fact]
    public async Task Normal_shutdown_joins_provider_cleanup_before_fixture_disposal_returns()
    {
        var finished = new DisposalSignal(); var blocked = new BlockingDisposal();
        using var server = new ServerFixture().KeepDataDir().WithServices(services =>
        {
            services.AddSingleton(_ => finished); services.AddSingleton(_ => blocked);
            var original = services.Last(d => d.ServiceType == typeof(ILoggerFactory)); services.Remove(original);
            services.AddSingleton<ILoggerFactory>(provider =>
            {
                _ = provider.GetRequiredService<DisposalSignal>(); // First constructed, last disposed, including the file logger.
                return (ILoggerFactory)original.ImplementationFactory!(provider);
            });
        });
        var provider = server.Services; _ = provider.GetRequiredService<BlockingDisposal>();
        string log = Assert.Single(Directory.GetFiles(Path.Combine(server.DataDir, "logs"), "server-*.log"));
        provider.GetRequiredService<IHostApplicationLifetime>().StopApplication();
        Assert.True(blocked.Entered.Wait(TimeSpan.FromSeconds(5)), "The real entry point did not start disposing its owned provider.");
        Task disposing = Task.Run(server.Dispose);
        try
        {
            Task first = await Task.WhenAny(disposing, Task.Delay(TimeSpan.FromMilliseconds(500)));
            Assert.NotSame(disposing, first);
            blocked.Release.Set();
            await disposing.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(finished.Disposed.Task.IsCompleted);
            using var exclusive = new FileStream(log, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        finally
        {
            blocked.Release.Set();
            await disposing.WaitAsync(TimeSpan.FromSeconds(5));
            await finished.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Directory.Delete(server.DataDir, recursive: true);
        }
    }

    private sealed class DisposalSignal : IDisposable
    {
        internal TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Dispose() => Disposed.TrySetResult();
    }
    private sealed class BlockingDisposal : IDisposable
    {
        internal ManualResetEventSlim Entered { get; } = new();
        internal ManualResetEventSlim Release { get; } = new();
        public void Dispose() { Entered.Set(); Release.Wait(TimeSpan.FromSeconds(15)); }
    }

    [Fact]
    public async Task Refused_startup_disposes_its_application_and_log_handle_before_returning_the_failure()
    {
        using var f = await SnapshotHarness.Create(); var root = f.H;
        await root.Db.Database.ExecuteSqlRawAsync("CREATE TABLE unqualified_future_catalog (Id INTEGER)");
        using (var server = HygieneFixtures.HygieneServer().WithoutWorld().WithDataDir(root.Data.Root).KeepDataDir()
            .WithSetting("Outposts:AnchorDirectory", Path.GetDirectoryName(root.Anchor.DatabasePath))
            .WithSetting("Outposts:StorageContract", "sqlite-local-v1"))
            Assert.NotNull(Record.Exception(() => _ = server.Services));
        var logs = Directory.GetFiles(Path.Combine(root.Data.Root, "logs"), "server-*.log"); Assert.NotEmpty(logs);
        foreach (string path in logs)
        {
            using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Assert.True(exclusive.Length > 0);
        }
        Assert.True(File.Exists(root.Anchor.DatabasePath));
    }
}
