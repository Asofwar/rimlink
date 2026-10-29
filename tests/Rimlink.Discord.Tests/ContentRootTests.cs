using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Rimlink.Discord.Tests;

[CollectionDefinition(nameof(CurrentDirectory), DisableParallelization = true)]
public sealed class CurrentDirectory;

/// <summary>Changes the process's current directory, so it runs alone.</summary>
[Collection(nameof(CurrentDirectory))]
public class ContentRootTests
{
    [Fact]
    public void ContentRootIsTheBotFolderWhereverItIsStarted()
    {
        // appsettings.json next to the assembly (spec §6) is what Check reads; the host must read the same file, also when
        // started from another folder (systemd, `dotnet /opt/bot/rimlink-discord.dll` from /).
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["RimlinkBot:Server:ApiToken"] = "discord-bot-token-0123456789",
            ["RimlinkBot:Discord:Token"] = "not-a-real-token",
        }).Build();
        string before = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(Path.GetTempPath());
        try
        {
            using var host = BotHost.Create([], config);

            Assert.Equal(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory),
                Path.TrimEndingDirectorySeparator(host.Services.GetRequiredService<IHostEnvironment>().ContentRootPath));
        }
        finally
        {
            Directory.SetCurrentDirectory(before);
        }
    }
}
