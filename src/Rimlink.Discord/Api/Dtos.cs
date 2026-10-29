using System.Text.Json;

namespace Rimlink.Discord.Api;

// The server's admin API shapes the bot reads (camelCase JSON). Only the fields the bot uses; the server pins them in
// tests/Rimlink.Server.Tests/Admin/BotContractTests.cs.

public sealed record PlayerRef(long Id, string Name);

/// <summary>One admin feed event; <see cref="Data"/> is the event's JSON body.</summary>
public sealed record FeedEvent(string? Id, string Type, JsonElement Data);

public sealed record PlayerPage(int Total, List<PlayerInfo> Items);

public sealed record PlayerInfo(long Id, string Name, string? FactionName, bool Online);

public sealed record SettlementInfo(long Id, int Tile, string Name);

public sealed record PlayerCard(long Id, string Name, string? FactionName, bool Online, bool Peaceful, List<SettlementInfo> Settlements);

public sealed record FreezeInfo(bool Frozen, string? Reason);

public sealed record ServerStatusInfo(string Version, int PlayersOnline, int MaxPlayers, bool WorldExists, FreezeInfo? Economy, FreezeInfo? Raids, string? CertificateFingerprint);

public sealed record LotInfo(long Id, long SellerPlayerId, string SellerName, long Price, string Summary, DateTime ExpiresAt);

public sealed record BanInfo(long Id, long? AccountId, long? SteamId, string? Ip, string Reason, DateTime? ExpiresAt, string? PlayerName);

public sealed record ActionInfo(long Id, DateTime At, string Action, string? ActorName, string? TargetName, string Details);

public sealed record RaidInfo(long Id, long AttackerPlayerId, long TargetPlayerId, string State, DateTime CreatedAt);

public sealed record RaidStatsInfo(int Queued, int Announced, int Landed, int ResolvedToday, int Expired, int Cancelled, bool Frozen, string? FreezeReason);

public sealed record LiveInfo(long Id, string Kind, long HostPlayerId, long GuestPlayerId, string State, string? EndReason);

public sealed record EconomyStatsInfo(long SilverInWallets, int OpenLots, int PendingParcels, int TradesToday, bool Frozen, string? FreezeReason);

public sealed record SaveInfo(int Version, long Size, long GameTick, bool IsCurrent, DateTime CreatedAt);

public sealed record ChatPosted(long Id);

public sealed record BanIssued(long Id);

public sealed record WalletBalance(long Balance);
