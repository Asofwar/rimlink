using System.Text;
using System.Text.Json;
using Rimlink.Discord.Api;
using Rimlink.Discord.Text;

namespace Rimlink.Discord.Feed;

/// <summary>One Discord line per feed event, in the configured language; everything players typed is escaped.</summary>
public sealed class Formatter(Strings s)
{
    private const char ZeroWidthSpace = '​';
    private const string Markdown = "\\*_~`|>[";

    /// <summary>
    /// Escapes Markdown and breaks mentions: a zero-width space after every <c>@</c> keeps <c>@everyone</c>, <c>@here</c> and
    /// <c>&lt;@id&gt;</c> from pinging even if a message is ever sent with mentions allowed.
    /// </summary>
    public static string Escape(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var sb = new StringBuilder(text.Length + 8);
        foreach (char c in text)
        {
            if (Markdown.Contains(c)) sb.Append('\\');
            sb.Append(c);
            if (c == '@') sb.Append(ZeroWidthSpace);
        }
        return sb.ToString();
    }

    public string? Format(FeedEvent e)
    {
        var d = e.Data;
        return e.Type switch
        {
            FeedTypes.ServerStarted => s.T("feed.server.started", Text(d, "version")),
            FeedTypes.PlayerJoined => s.T("feed.player.joined", Name(d, "player")),
            FeedTypes.PlayerLeft => s.T("feed.player.left", Name(d, "player")),
            FeedTypes.ColonyFounded => s.T("feed.colony.founded", Name(d, "player"), Text(d, "colony")),
            FeedTypes.ColonyRemoved => s.T("feed.colony.removed", Name(d, "player"), Text(d, "colony")),
            FeedTypes.Notice => s.T("feed.notice", Text(d, "text")),
            FeedTypes.ServerShutdown => s.T("feed.server.shutdown", Number(d, "inSeconds"), Text(d, "reason")),
            FeedTypes.EconomyFrozen => Bool(d, "frozen") ? s.T("feed.economy.frozen", Reason(d)) : s.T("feed.economy.unfrozen"),
            FeedTypes.RaidsFrozen => Bool(d, "frozen") ? s.T("feed.raids.frozen", Reason(d)) : s.T("feed.raids.unfrozen"),
            FeedTypes.RaidLanded => s.T("feed.raid.landed", Name(d, "attacker"), Name(d, "target")),
            FeedTypes.RaidResolved => s.T("feed.raid.resolved", Name(d, "attacker"), Name(d, "target"),
                Number(d, "dead"), Number(d, "captured"), Number(d, "fled"), Number(d, "alive"), Number(d, "lootValue")),
            FeedTypes.VisitStarted => s.T("feed.visit.started", Name(d, "visitor"), Name(d, "host")),
            FeedTypes.LiveStarted => s.T($"feed.live.started.{Kind(d)}", Name(d, "guest"), Name(d, "host")),
            FeedTypes.LiveEnded => s.T($"feed.live.ended.{Kind(d)}", Name(d, "guest"), Name(d, "host"), LiveReason(d)),
            FeedTypes.LotPosted => s.T("feed.lot.posted", Name(d, "seller"), Text(d, "summary"), Number(d, "price"), Number(d, "lotId")),
            FeedTypes.LotSold => s.T("feed.lot.sold", Name(d, "buyer"), Text(d, "summary"), Name(d, "seller"), Number(d, "price")),
            FeedTypes.ShopSold => s.T("feed.shop.sold", Name(d, "buyer"), Text(d, "item"), Number(d, "count"), Name(d, "host"), Number(d, "price")),
            FeedTypes.AdminAction => s.T("feed.admin.action", Text(d, "actor"), Text(d, "action"),
                Has(d, "target") ? s.T("feed.admin.target", Name(d, "target")) : "", Text(d, "details")),
            FeedTypes.IntegrityAlert => Integrity(d),
            _ => null,
        };
    }

    private string Integrity(JsonElement d)
    {
        string line = Text(d, "kind") == "devmode"
            ? s.T("feed.integrity.devmode", Name(d, "player"), Number(d, "version"))
            : s.T("feed.integrity.wealth", Name(d, "player"), Number(d, "rate"), Number(d, "version"), Number(d, "net"), Number(d, "days"));
        return Bool(d, "frozen") ? line + s.T("feed.integrity.frozen") : line;
    }

    private string Reason(JsonElement d) => Has(d, "reason") ? Text(d, "reason") : s.T("reason.none");

    private string LiveReason(JsonElement d)
    {
        string reason = d.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString()! : "";
        return s.Maybe($"live.reason.{reason}") ?? Escape(reason);
    }

    private static string Kind(JsonElement d) => d.TryGetProperty("kind", out var k) && k.GetString() == "visit" ? "visit" : "raid";

    private static bool Has(JsonElement d, string name) =>
        d.ValueKind == JsonValueKind.Object && d.TryGetProperty(name, out var v) && v.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);

    private static string Text(JsonElement d, string name) =>
        Has(d, name) && d.GetProperty(name).ValueKind == JsonValueKind.String ? Escape(d.GetProperty(name).GetString()) : "";

    private static string Name(JsonElement d, string player) => Has(d, player) ? Text(d.GetProperty(player), "name") : "";

    private static string Number(JsonElement d, string name) => Has(d, name) ? d.GetProperty(name).GetRawText() : "0";

    private static bool Bool(JsonElement d, string name) => Has(d, name) && d.GetProperty(name).ValueKind == JsonValueKind.True;
}
