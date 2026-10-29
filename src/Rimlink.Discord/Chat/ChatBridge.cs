using System.Text.Json;
using Rimlink.Discord.Api;
using Rimlink.Discord.Feed;
using Rimlink.Discord.Gateway;
using Rimlink.Discord.Text;

namespace Rimlink.Discord.Chat;

/// <summary>A message in a Discord channel, as the bridge needs it (the Discord.Net shell fills it in).</summary>
public sealed record DiscordChatMessage(ulong ChannelId, ulong MessageId, bool AuthorIsBot, string AuthorName, string CleanContent, int Attachments);

/// <summary>
/// The public game chat and one Discord channel, both ways (Discord bot spec §5). Game → Discord: player lines only (system lines
/// are in the server group already; external lines are the bot's own echo). Discord → game: POST /chat, author and text cut to
/// the server's limits.
/// </summary>
public sealed class ChatBridge(ChannelMap channels, OutboundQueue queue, RimlinkApi api, IDiscordOut discord, Strings s, ILogger<ChatBridge> log)
{
    /// <summary>The server's <c>Limits.ChatMaxLength</c>; the bot does not reference the protocol assembly.</summary>
    public const int MaxLength = 500;

    public const int AuthorMaxLength = 32;

    public void FromGame(FeedEvent e)
    {
        var d = e.Data;
        if (d.ValueKind != JsonValueKind.Object || !d.TryGetProperty("kind", out var kind) || kind.GetString() != "player") return;
        string name = d.TryGetProperty("from", out var from) && from.ValueKind == JsonValueKind.Object && from.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
        string text = d.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
        queue.Enqueue(channels.Chat, $"**{Formatter.Escape(name)}**: {Formatter.Escape(text)}");
    }

    /// <summary>Posts a Discord message into the game chat. False when it is not for the bridge, empty, or refused (then ❌ on it).</summary>
    public async Task<bool> FromDiscord(DiscordChatMessage m, CancellationToken ct)
    {
        if (channels.Chat == 0 || m.ChannelId != channels.Chat || m.AuthorIsBot) return false;

        string text = m.CleanContent.Trim();
        if (m.Attachments > 0) text = text.Length == 0 ? s.T("chat.attachment") : $"{text} {s.T("chat.attachment")}";
        if (text.Length == 0) return false;
        if (text.Length > MaxLength) text = text[..(MaxLength - 1)] + "…";
        string author = m.AuthorName.Trim();
        if (author.Length > AuthorMaxLength) author = author[..AuthorMaxLength];

        try
        {
            await api.PostChat(author, text, ct);
            return true;
        }
        catch (RimlinkApiException ex)
        {
            log.LogWarning("Chat line from Discord refused: {Status} {Detail}", ex.Status, ex.Detail);
            await discord.React(m.ChannelId, m.MessageId, "❌", ct);
            return false;
        }
    }
}
