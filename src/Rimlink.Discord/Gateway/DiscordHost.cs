using System.Reflection;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Microsoft.Extensions.Options;
using Rimlink.Discord.Chat;
using Rimlink.Discord.Feed;
using Rimlink.Discord.Options;

namespace Rimlink.Discord.Gateway;

/// <summary>
/// The Discord side: logs in, checks the configured guild, channels and roles once ready (a channel the bot cannot use turns its
/// group off), registers the guild commands, and routes interactions and chat-channel messages.
/// </summary>
public sealed class DiscordHost(
    DiscordSocketClient client,
    InteractionService interactions,
    IServiceProvider services,
    IOptions<BotOptions> options,
    ChannelMap channels,
    ChatBridge chat,
    ILogger<DiscordHost> log) : BackgroundService
{
    private readonly DiscordSection discord = options.Value.Discord;
    private int registered;
    private CancellationToken stopping;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        stopping = ct;
        client.Log += Log;
        interactions.Log += Log;
        client.Ready += OnReady;
        client.InteractionCreated += OnInteraction;
        client.MessageReceived += OnMessage;

        await interactions.AddModulesAsync(Assembly.GetExecutingAssembly(), services);
        await client.LoginAsync(TokenType.Bot, discord.Token);
        await client.StartAsync();
        try
        {
            await Task.Delay(Timeout.Infinite, ct);
        }
        catch (OperationCanceledException)
        {
        }
        await client.StopAsync();
        await client.LogoutAsync();
    }

    private async Task OnReady()
    {
        if (Interlocked.Exchange(ref registered, 1) == 1) return; // Ready fires again after every reconnect
        var guild = client.GetGuild(discord.GuildId);
        if (guild is null)
        {
            log.LogError("The bot is not in the Discord server {Guild} (RimlinkBot:Discord:GuildId); no commands registered", discord.GuildId);
            return;
        }

        foreach (var group in Enum.GetValues<FeedGroup>())
        {
            ulong id = channels.For(group);
            if (id == 0) continue;
            var channel = guild.GetTextChannel(id);
            var permissions = channel is null ? default : guild.CurrentUser.GetPermissions(channel);
            if (channel is not null && permissions.ViewChannel && permissions.SendMessages) continue;
            log.LogWarning("Channel {Channel} for the {Group} group is missing or the bot cannot write there; the group is off", id, group);
            channels.Disable(group);
        }
        foreach (ulong role in discord.ModeratorRoleIds.Concat(discord.AdminRoleIds))
            if (guild.GetRole(role) is null) log.LogWarning("Role {Role} from RimlinkBot:Discord is not in the Discord server", role);

        await interactions.RegisterCommandsToGuildAsync(discord.GuildId);
        log.LogInformation("Registered {Count} commands in {Guild}", interactions.SlashCommands.Count, guild.Name);
    }

    private async Task OnInteraction(SocketInteraction interaction)
    {
        var result = await interactions.ExecuteCommandAsync(new SocketInteractionContext(client, interaction), services);
        if (!result.IsSuccess) log.LogWarning("Interaction failed: {Error} {Reason}", result.Error, result.ErrorReason);
    }

    private Task OnMessage(SocketMessage message)
    {
        if (message is not SocketUserMessage user || message.Channel.Id != channels.Chat || channels.Chat == 0) return Task.CompletedTask;
        string name = (message.Author as SocketGuildUser)?.DisplayName ?? message.Author.GlobalName ?? message.Author.Username;
        var line = new DiscordChatMessage(message.Channel.Id, message.Id, message.Author.IsBot || message.Author.IsWebhook, name, user.CleanContent, message.Attachments.Count);
        // Off the gateway task: a slow server must not stall Discord's event loop.
        _ = Task.Run(async () =>
        {
            try
            {
                await chat.FromDiscord(line, stopping);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Chat line from Discord failed");
            }
        });
        return Task.CompletedTask;
    }

    private Task Log(LogMessage m)
    {
        var level = m.Severity switch
        {
            LogSeverity.Critical => LogLevel.Critical,
            LogSeverity.Error => LogLevel.Error,
            LogSeverity.Warning => LogLevel.Warning,
            LogSeverity.Info => LogLevel.Information,
            _ => LogLevel.Debug,
        };
        log.Log(level, m.Exception, "Discord {Source}: {Message}", m.Source, m.Message);
        return Task.CompletedTask;
    }
}
