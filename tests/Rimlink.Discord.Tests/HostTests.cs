using Discord.Interactions;
using Discord.Rest;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Rimlink.Discord.Chat;
using Rimlink.Discord.Commands;
using Rimlink.Discord.Feed;
using Rimlink.Discord.Gateway;
using Xunit;

namespace Rimlink.Discord.Tests;

public class HostTests
{
    private static IConfiguration Valid() => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["RimlinkBot:Server:Url"] = "https://rimlink:25560",
        ["RimlinkBot:Server:ApiToken"] = "discord-bot-token-0123456789",
        ["RimlinkBot:Discord:Token"] = "not-a-real-token",
        ["RimlinkBot:Discord:GuildId"] = "1",
        ["RimlinkBot:Discord:Language"] = "ru",
        ["RimlinkBot:Discord:Channels:Chat"] = "14",
    }).Build();

    /// <summary>Every slash command of the bot spec §5, as Discord shows it ("group command" for subcommands).</summary>
    private static readonly string[] Commands =
    [
        "online", "status", "market", "player",
        "kick", "mute", "unmute", "broadcast", "bans", "actions",
        "ban", "pardon", "wallet", "peaceful", "saves", "shutdown", "rollback", "reset", "recreateworld",
        "raids list", "raids stats", "raids cancel", "raids freeze", "raids unfreeze",
        "live list", "live end", "economy stats", "economy freeze", "economy unfreeze", "refund lot", "refund parcel",
    ];

    [Fact]
    public void HostResolvesEveryService()
    {
        using var host = BotHost.Create([], Valid());

        Assert.NotNull(host.Services.GetRequiredService<CommandHandlers>());
        Assert.NotNull(host.Services.GetRequiredService<FeedRouter>());
        Assert.NotNull(host.Services.GetRequiredService<ChatBridge>());
        Assert.Equal(14UL, host.Services.GetRequiredService<ChannelMap>().Chat);
        var hosted = host.Services.GetServices<IHostedService>().Select(s => s.GetType()).ToList();
        Assert.Contains(typeof(DiscordHost), hosted);
        Assert.Contains(typeof(FeedWorker), hosted);
    }

    [Fact]
    public async Task ModulesBuild()
    {
        using var host = BotHost.Create([], Valid());
        using var interactions = new InteractionService(new DiscordRestClient());

        await interactions.AddModulesAsync(typeof(BotHost).Assembly, host.Services);

        var names = interactions.SlashCommands.Select(c => c.Module.IsSlashGroup ? $"{c.Module.SlashGroupName} {c.Name}" : c.Name).ToList();
        Assert.Equal(Commands.Order(), names.Order());
        foreach (var command in interactions.SlashCommands)
        {
            Assert.Matches("^[a-z0-9_-]{1,32}$", command.Name);
            Assert.All(command.Parameters, p => Assert.Matches("^[a-z0-9_-]{1,32}$", p.Name));
            Assert.InRange(command.Description.Length, 1, 100);
            // A default member permission would make Discord refuse the command to a moderator without it; the roles are the gate.
            Assert.Null(command.DefaultMemberPermissions);
            Assert.Null(command.Module.DefaultMemberPermissions);
        }
        Assert.Equal(2, interactions.ComponentCommands.Count); // confirm:* and cancel:*
    }

    [Fact]
    public void StateFileSeedsTheReader()
    {
        string file = Path.Combine(Path.GetTempPath(), "rimlink-bot-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "1700000000:42");
        var config = new ConfigurationBuilder().AddConfiguration(Valid()).AddInMemoryCollection(new Dictionary<string, string?> { ["RimlinkBot:StateFile"] = file }).Build();

        using var host = BotHost.Create([], config);

        Assert.Equal("1700000000:42", host.Services.GetRequiredService<Rimlink.Discord.Api.FeedReader>().LastId);
        File.Delete(file);
    }

    [Fact]
    public void InvalidConfigurationExitsWithErrors()
    {
        var output = new StringWriter();

        int code = BotHost.Check(new ConfigurationBuilder().Build(), output);

        Assert.Equal(1, code);
        Assert.Contains("Server:ApiToken", output.ToString());
        Assert.Contains("Discord:Token", output.ToString());
        Assert.Equal(0, BotHost.Check(Valid(), new StringWriter()));
    }

    [Fact]
    public void RussianCommandDescriptionsCoverEveryCommand()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Strings", "commands.ru.json");
        var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path)).RootElement;

        foreach (string command in Commands)
        {
            var node = json;
            foreach (string part in command.Split(' ')) Assert.True(node.TryGetProperty(part, out node), $"commands.ru.json lacks {command}");
            Assert.True(node.TryGetProperty("description", out _), $"{command} has no Russian description");
        }
    }
}
