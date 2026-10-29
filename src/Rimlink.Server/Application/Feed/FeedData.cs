namespace Rimlink.Server.Application.Feed;

/// <summary>Event types of the admin feed (Discord bot spec §2). New types are added; existing ones keep their name and fields.</summary>
public static class FeedTypes
{
    public const string ServerStarted = "server.started";
    public const string PlayerJoined = "player.joined";
    public const string PlayerLeft = "player.left";
    public const string ColonyFounded = "colony.founded";
    public const string ColonyRemoved = "colony.removed";
    public const string ColonyArchived = "colony.archived";
    public const string ColonyRestored = "colony.restored";
    public const string Notice = "notice";
    public const string ServerShutdown = "server.shutdown";
    public const string EconomyFrozen = "economy.frozen";
    public const string RaidsFrozen = "raids.frozen";
    public const string RaidLanded = "raid.landed";
    public const string RaidResolved = "raid.resolved";
    public const string VisitStarted = "visit.started";
    public const string LiveStarted = "live.started";
    public const string LiveEnded = "live.ended";
    public const string LotPosted = "lot.posted";
    public const string LotSold = "lot.sold";
    public const string ShopSold = "shop.sold";
    public const string AdminAction = "admin.action";
    public const string ChatMessage = "chat.message";
    public const string IntegrityAlert = "integrity.alert";

    public static readonly IReadOnlySet<string> All = new HashSet<string>
    {
        ServerStarted, PlayerJoined, PlayerLeft, ColonyFounded, ColonyRemoved, ColonyArchived, ColonyRestored, Notice, ServerShutdown, EconomyFrozen, RaidsFrozen,
        RaidLanded, RaidResolved, VisitStarted, LiveStarted, LiveEnded, LotPosted, LotSold, ShopSold, AdminAction, ChatMessage, IntegrityAlert,
    };
}

public sealed record FeedPlayer(long Id, string Name);

public sealed record ServerStartedData(string Version);

public sealed record PlayerData(FeedPlayer Player, string Faction);

public sealed record ColonyData(FeedPlayer Player, string Colony, int? Tile);

/// <summary><paramref name="Reason"/> is <c>auto</c> (the sweep) or <c>admin</c>; <paramref name="Actor"/> the audit actor name.</summary>
public sealed record ColonyArchivedData(FeedPlayer Player, string Colony, int Tile, string Reason, string Actor);

/// <summary><paramref name="Actor"/> is <c>player</c> for the restore at login, otherwise the admin actor name.</summary>
public sealed record ColonyRestoredData(FeedPlayer Player, string Colony, int Tile, string Actor);

public sealed record NoticeData(string Text);

public sealed record ShutdownData(int InSeconds, string Reason);

public sealed record FreezeData(bool Frozen, string? Reason);

public sealed record RaidLandedData(long RaidId, FeedPlayer Attacker, FeedPlayer Target);

public sealed record RaidResolvedData(long RaidId, FeedPlayer Attacker, FeedPlayer Target, int Dead, int Captured, int Fled, int Alive, long LootValue, int Captives);

public sealed record VisitData(FeedPlayer Visitor, FeedPlayer Host);

public sealed record LiveData(long SessionId, string Kind, FeedPlayer Host, FeedPlayer Guest, string? Reason);

public sealed record LotData(long LotId, FeedPlayer Seller, FeedPlayer? Buyer, long Price, string Summary);

public sealed record ShopSoldData(FeedPlayer Host, FeedPlayer Buyer, string Item, int Count, long Price);

public sealed record AdminActionData(string Action, string Actor, FeedPlayer? Target, string Details);

/// <summary><c>kind</c>: devmode | wealth-warn | wealth-freeze; the numbers are null for devmode.</summary>
public sealed record IntegrityAlertData(FeedPlayer Player, string Kind, int Version, long? Net, double? Days, long? Rate, bool Frozen);

public sealed record ChatData(long Id, string Kind, FeedPlayer? From, string? Author, string Text);
