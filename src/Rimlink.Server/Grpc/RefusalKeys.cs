namespace Rimlink.Server.Grpc;

/// <summary>
/// Keys of the refusals a player sees (live UX spec §5): the client shows <c>Rimlink.Refusal.&lt;key&gt;</c> from the mod's keyed strings
/// with the refusal's arguments, in the player's language, and falls back to the English message. A value never changes once shipped.
/// </summary>
public static class RefusalKeys
{
    public const string RateLimited = "RateLimited";
    public const string NoSuchPlayer = "Common.NoSuchPlayer";

    public const string ChatEmpty = "Chat.Empty";
    public const string ChatTooLong = "Chat.TooLong";
    public const string ChatControlChars = "Chat.ControlChars";
    public const string ChatMuted = "Chat.Muted";
    public const string ChatNotOnline = "Chat.NotOnline";
    public const string PingTile = "Chat.PingTile";
    public const string PingCell = "Chat.PingCell";

    public const string LiveDisabled = "Live.Disabled";
    public const string LiveSelf = "Live.Self";
    public const string LiveNoPawns = "Live.NoPawns";
    public const string LiveTooManyPawns = "Live.TooManyPawns";
    public const string LiveHostOffline = "Live.HostOffline";
    public const string LiveOwnSession = "Live.OwnSession";
    public const string LiveHostBusy = "Live.HostBusy";
    public const string LiveOnVisit = "Live.OnVisit";
    public const string LiveInviteGone = "Live.InviteGone";
    public const string LiveOver = "Live.Over";
    public const string LiveNotOpen = "Live.NotOpen";
    public const string LiveNoVisit = "Live.NoVisit";
    public const string LiveNone = "Live.None";
    public const string LiveNoSuchSession = "Live.NoSuchSession";

    public const string VisitsClosed = "Visits.Closed";
    public const string VisitsSelf = "Visits.Self";
    public const string VisitsInLive = "Visits.InLive";
    public const string VisitsNoColony = "Visits.NoColony";
    public const string ShopSelf = "Shop.Self";
    public const string ShopCountTooLow = "Shop.CountTooLow";
    public const string ShopNoItemData = "Shop.NoItemData";
    public const string ShopSold = "Shop.Sold";
    public const string ShopOnlyLeft = "Shop.OnlyLeft";
    public const string ShopMismatch = "Shop.Mismatch";
}
