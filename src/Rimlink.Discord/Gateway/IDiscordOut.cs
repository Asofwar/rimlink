namespace Rimlink.Discord.Gateway;

/// <summary>What the bot sends to Discord. Every message goes out without mentions; a fake stands in for Discord in tests.</summary>
public interface IDiscordOut
{
    Task Send(ulong channelId, string text, CancellationToken ct);

    Task React(ulong channelId, ulong messageId, string emoji, CancellationToken ct);
}
