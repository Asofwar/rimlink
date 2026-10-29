using System.Text.Json;
using Rimlink.Discord.Api;
using Rimlink.Discord.Gateway;

namespace Rimlink.Discord.Tests.Support;

/// <summary>Records what the bot would send to Discord.</summary>
public sealed class FakeDiscordOut : IDiscordOut
{
    public List<(ulong Channel, string Text)> Sent { get; } = [];
    public List<(ulong Channel, ulong Message, string Emoji)> Reactions { get; } = [];
    public bool Fail { get; set; }

    public Task Send(ulong channelId, string text, CancellationToken ct)
    {
        if (Fail) throw new InvalidOperationException("discord is down");
        lock (Sent) Sent.Add((channelId, text));
        return Task.CompletedTask;
    }

    public Task React(ulong channelId, ulong messageId, string emoji, CancellationToken ct)
    {
        lock (Reactions) Reactions.Add((channelId, messageId, emoji));
        return Task.CompletedTask;
    }

    public List<string> In(ulong channel) => Sent.Where(s => s.Channel == channel).Select(s => s.Text).ToList();
}

public static class Events
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    public static FeedEvent Ev(string type, object data, string? id = "1:1") => new(id, type, JsonSerializer.SerializeToElement(data, Web));

    public static object P(long id, string name) => new { id, name };
}
