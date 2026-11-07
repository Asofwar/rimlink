using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Rimlink.Server.Tests.Support;
using Xunit;

namespace Rimlink.Server.Tests.Hosting;

public sealed class HostLoggingTests
{
    [Fact]
    public void Startup_diagnostic_can_read_a_log_while_its_writer_is_still_closing()
    {
        using var fixture = new ServerFixture();
        string directory = Path.Combine(fixture.DataDir, "logs");
        Directory.CreateDirectory(directory);
        using var writer = new FileStream(Path.Combine(directory, "server-live.log"), FileMode.CreateNew,
            FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        const string marker = "original startup failure";
        writer.Write(System.Text.Encoding.UTF8.GetBytes(marker));
        writer.Flush();

        Assert.Contains(marker, fixture.ReadStartupLog());
    }

    [Fact]
    public async Task Disposing_one_host_releases_its_logs_without_closing_another_hosts_logger()
    {
        using var first = new ServerFixture().KeepDataDir();
        using var firstClient = first.CreateClient();
        using var second = new ServerFixture();
        using var secondClient = second.CreateClient();
        first.Dispose();

        string[] files = Directory.GetFiles(Path.Combine(first.DataDir, "logs"), "server-*.log");
        Assert.NotEmpty(files);
        foreach (string file in files)
        {
            using var exclusive = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        Directory.Delete(first.DataDir, recursive: true);

        string marker = "host-owned-logger-" + Guid.NewGuid().ToString("N");
        second.Services.GetRequiredService<ILogger<HostLoggingTests>>().LogInformation("{Marker}", marker);
        Assert.True((await secondClient.GetAsync("/health")).IsSuccessStatusCode);
        Assert.Contains(marker, string.Join("\n", Directory.GetFiles(Path.Combine(second.DataDir, "logs"), "server-*.log").Select(ReadLiveLog)));
        Assert.Contains("HTTP GET /health", string.Join("\n", Directory.GetFiles(Path.Combine(second.DataDir, "logs"), "server-*.log").Select(ReadLiveLog)));
    }

    [Theory]
    [InlineData("Admin:FeedBufferSize", "10", "FeedBufferSize")]
    [InlineData("Admin:Tokens:0:Token", "short", "Admin:Tokens[0]")]
    public async Task Invalid_startup_preserves_original_failure_after_host_cleanup(string key, string value, string expected)
    {
        await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() =>
        {
            var fixture = new ServerFixture().KeepDataDir()
                .WithToken(0, "bot", "bot-token-0123456789abcdefgh", "read")
                .WithSetting(key, value);
            try
            {
                Exception exception;
                // The test host can surface startup refusal while the provider still owns the log.
                using (fixture)
                    exception = Assert.ThrowsAny<Exception>(() => fixture.CreateClient());
                string diagnostic = fixture.StartupFailureDiagnostic(exception);
                Assert.True(diagnostic.Contains(expected, StringComparison.Ordinal), diagnostic);
                Assert.Contains("Server initialization failed", fixture.ReadStartupLog());
                foreach (string file in Directory.GetFiles(Path.Combine(fixture.DataDir, "logs"), "server-*.log"))
                {
                    using var exclusive = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                }
            }
            finally
            {
                if (Directory.Exists(fixture.DataDir)) Directory.Delete(fixture.DataDir, recursive: true);
            }
        })));
    }

    private static string ReadLiveLog(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
