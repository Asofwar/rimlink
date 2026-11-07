using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Hosting;
using Xunit;

namespace Rimlink.Server.Tests.Outposts;

public sealed class OutpostAcceptanceCompositionClockTests
{
    [Fact]
    public async Task Private_composition_changes_only_the_outpost_clock_and_preserves_system_time_services()
    {
        string root = Path.Combine(Path.GetTempPath(), "rimlink-clock-composition-" + Guid.NewGuid().ToString("N"));
        string? pool = null;
        try
        {
            var own = new FixedTime(); var composition = new Composition(root, own);
            await using var app = await RimlinkApplication.BuildIsolatedAsync([], composition);
            using (var scope = app.Services.CreateScope())
                pool = scope.ServiceProvider.GetRequiredService<Rimlink.Server.Storage.RimlinkDbContext>().Database.GetConnectionString();
            Assert.Same(TimeProvider.System, app.Services.GetRequiredService<TimeProvider>());
            Assert.Equal(FixedTime.Utc.UtcDateTime, app.Services.GetRequiredService<OutpostClock>().Sample().Utc);
        }
        finally
        {
            if (pool is not null) Microsoft.Data.Sqlite.SqliteConnection.ClearPool(new Microsoft.Data.Sqlite.SqliteConnection(pool));
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private sealed class FixedTime : TimeProvider
    {
        internal static readonly DateTimeOffset Utc = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Utc;
        public override long GetTimestamp() => 0;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    }
    private sealed class Composition(string root, TimeProvider time) : IOutpostAcceptanceComposition, IOutpostBuildQualification
    {
        public IOutpostBuildQualification Qualification => this;
        public TimeProvider OutpostTimeProvider => time;
        public void ConfigureConfiguration(ConfigurationManager configuration)
        {
            configuration.Sources.Clear();
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            { ["Server:DataDir"] = root, ["Backup:IntervalHours"] = "0" });
        }
        public void ValidateConfiguration(ServerOptions server, TlsOptions tls, IConfiguration configuration) { }
        public OutpostBuildQualification Observe() => new(OutpostRuntimeMode.IsolatedAcceptance, OutpostQualificationState.Pending,
            OutpostProductQualification.SourceIdentity, true, true); // Explicit synthetic unit composition, never G-case evidence.
    }
}
