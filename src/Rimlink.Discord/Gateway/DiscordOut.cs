using Discord;
using Discord.WebSocket;

namespace Rimlink.Discord.Gateway;

/// <summary>Sends through the gateway client; never with mentions allowed.</summary>
public sealed class DiscordOut(DiscordSocketClient client) : IDiscordOut
{
    public async Task Send(ulong channelId, string text, CancellationToken ct)
    {
        if (await client.GetChannelAsync(channelId, new RequestOptions { CancelToken = ct }) is not IMessageChannel channel)
            throw new InvalidOperationException($"channel {channelId} is not a text channel the bot can see");
        await channel.SendMessageAsync(text, allowedMentions: AllowedMentions.None, options: new RequestOptions { CancelToken = ct });
    }

    public async Task React(ulong channelId, ulong messageId, string emoji, CancellationToken ct)
    {
        var options = new RequestOptions { CancelToken = ct };
        if (await client.GetChannelAsync(channelId, options) is IMessageChannel channel && await channel.GetMessageAsync(messageId, options: options) is { } message)
            await message.AddReactionAsync(new Emoji(emoji), options);
    }
}
