using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Rimlink.Server.Tests.Support;
using Xunit;

namespace Rimlink.Server.Tests.Hosting;

public sealed class HostLoggingTests
{
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
            using var fixture = new ServerFixture()
                .WithToken(0, "bot", "bot-token-0123456789abcdefgh", "read")
                .WithSetting(key, value);
            var exception = Assert.ThrowsAny<Exception>(() => fixture.CreateClient());
            string diagnostic = fixture.StartupFailureDiagnostic(exception);
            Assert.True(diagnostic.Contains(expected, StringComparison.Ordinal), diagnostic);
            Assert.Contains("Server initialization failed", fixture.ReadStartupLog());
            foreach (string file in Directory.GetFiles(Path.Combine(fixture.DataDir, "logs"), "server-*.log"))
            {
                using var exclusive = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
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
