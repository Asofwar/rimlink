namespace Rimlink.Discord.Feed;

/// <summary>The admin feed's event types (Discord bot spec §2) and the channel group each goes to.</summary>
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

    private static readonly Dictionary<string, FeedGroup> Groups = new()
    {
        [ServerStarted] = FeedGroup.Server, [PlayerJoined] = FeedGroup.Server, [PlayerLeft] = FeedGroup.Server,
        [ColonyFounded] = FeedGroup.Server, [ColonyRemoved] = FeedGroup.Server, [Notice] = FeedGroup.Server,
        [ColonyArchived] = FeedGroup.Server, [ColonyRestored] = FeedGroup.Server,
        [ServerShutdown] = FeedGroup.Server, [EconomyFrozen] = FeedGroup.Server, [RaidsFrozen] = FeedGroup.Server,
        [RaidLanded] = FeedGroup.Raids, [RaidResolved] = FeedGroup.Raids, [VisitStarted] = FeedGroup.Raids,
        [LiveStarted] = FeedGroup.Raids, [LiveEnded] = FeedGroup.Raids,
        [LotPosted] = FeedGroup.Market, [LotSold] = FeedGroup.Market, [ShopSold] = FeedGroup.Market,
        [AdminAction] = FeedGroup.Moderation, [IntegrityAlert] = FeedGroup.Moderation,
        [ChatMessage] = FeedGroup.Chat,
    };

    public static IReadOnlyCollection<string> All => Groups.Keys;

    /// <summary>The group of a known type; null for a type this bot does not know (a newer server).</summary>
    public static FeedGroup? GroupOf(string type) => Groups.TryGetValue(type, out var group) ? group : null;
}

public enum FeedGroup { Server, Raids, Market, Moderation, Chat }

/// <summary>
/// Discord channel per group; 0 = the group is off (not configured, or the bot cannot write there — <see cref="Disable"/>, called
/// by the start-up check once Discord is connected).
/// </summary>
public sealed class ChannelMap(ulong server, ulong raids, ulong market, ulong moderation, ulong chat)
{
    private readonly ulong[] ids = [server, raids, market, moderation, chat];

    public ulong Server => For(FeedGroup.Server);
    public ulong Raids => For(FeedGroup.Raids);
    public ulong Market => For(FeedGroup.Market);
    public ulong Moderation => For(FeedGroup.Moderation);
    public ulong Chat => For(FeedGroup.Chat);

    public ulong For(FeedGroup group) => Volatile.Read(ref ids[(int)group]);

    public void Disable(FeedGroup group) => Volatile.Write(ref ids[(int)group], 0UL);
}
