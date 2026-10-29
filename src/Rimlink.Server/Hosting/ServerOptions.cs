namespace Rimlink.Server.Hosting;

public sealed class ServerOptions
{
    public const string Section = "Server";

    public int Port { get; set; } = 25560;
    public string DataDir { get; set; } = "./data";
    public int MaxPlayers { get; set; } = 50;
    public string? ServerPassword { get; set; }
    public int MaxSettlementsPerPlayer { get; set; } = 1;
    public int ProtocolVersion => Protocol.ProtocolVersion.Current;
}

public sealed class TlsOptions
{
    public const string Section = "Tls";

    public bool Required { get; set; } = true;
    /// <summary>PEM certificate; used together with <see cref="KeyPath"/>. Empty = self-signed.</summary>
    public string? CertificatePath { get; set; }
    public string? KeyPath { get; set; }
    /// <summary>Where the generated self-signed certificate lives; default &lt;DataDir&gt;/tls/server.pfx.</summary>
    public string? SelfSignedPath { get; set; }
}

public sealed class AuthOptions
{
    public const string Section = "Auth";

    public bool AllowPassword { get; set; }
    public string? SteamWebApiKey { get; set; }
    public long[] AdminSteamIds { get; set; } = [];
    public string[] AdminLogins { get; set; } = [];
}

/// <summary>Economy limits (sub-project 2 spec §3); mirrored for the client in <c>Rimlink.Protocol.Limits.Economy*</c>.</summary>
public sealed class EconomyOptions
{
    public const string Section = "Economy";

    public bool Enabled { get; set; } = true;
    public int MaxParcelBytes { get; set; } = Protocol.Limits.EconomyMaxParcelBytes;
    public int MaxItemsPerParcel { get; set; } = Protocol.Limits.EconomyMaxItemsPerParcel;
    public int MaxPawnsPerParcel { get; set; } = Protocol.Limits.EconomyMaxPawnsPerParcel;
    public int MaxLotsPerPlayer { get; set; } = 20;
    public int MaxPendingParcels { get; set; } = 50;
    public int LotLifetimeDays { get; set; } = 30;
    public int MarketFeePercent { get; set; } = 0;
    public int SendCooldownSeconds { get; set; } = 5;
    /// <summary>A gift without an answer for this long is refused and goes back to its sender.</summary>
    public int GiftDecisionDays { get; set; } = 7;
    /// <summary>An open trade expires after this long; both sides go back.</summary>
    public int TradeLifetimeDays { get; set; } = 7;
    /// <summary>Open trades a player may take part in at once.</summary>
    public int MaxOpenTrades { get; set; } = 5;
    /// <summary>Share of server-held wealth (wallet, open lots, parcels in transit) counted as colony wealth; 0–1000, clamped at use.</summary>
    public int ServerWealthPercent { get; set; } = 100;
    /// <summary>Refuse deposits, new lots and sends while a raid or a live raid is on the player.</summary>
    public bool BlockOutflowUnderThreat { get; set; } = true;
    /// <summary>Daily fee on pawns and perishables held on the server, percent of their value; 0 turns it off.</summary>
    public double StorageFeePercentPerDay { get; set; } = 0.5;
    public double StorageFreeDays { get; set; } = 3;
}

/// <summary>Block list and the other personal social settings.</summary>
public sealed class SocialOptions
{
    public const string Section = "Social";

    public int MaxBlocks { get; set; } = 200;
}

/// <summary>Raid rules (sub-project 3a spec §3).</summary>
public sealed class RaidOptions
{
    public const string Section = "Raids";

    public bool Enabled { get; set; } = true;
    public int MaxRaiders { get; set; } = 10;
    public int MaxActiveRaidsPerAttacker { get; set; } = 1;
    public int TargetCooldownHours { get; set; } = 8;
    public int PairCooldownHours { get; set; } = 24;
    public int NewbieProtectionDays { get; set; } = 3;
    public long MinColonyWealth { get; set; } = 20000;
    public int LootCapPercent { get; set; } = 10;
    public long LootCapSilver { get; set; } = 5000;
    public bool AllowKidnap { get; set; }
    public int WarnTicks { get; set; } = 60000;
    public int TimeoutDays { get; set; } = 3;
    public int ExpireRealDays { get; set; } = 7;
    public int PeacefulOffDelayHours { get; set; } = 24;
    /// <summary>A visit ticket nobody ended (the visitor's game crashed) stops working after this many real hours.</summary>
    public int VisitHours { get; set; } = 12;
}

/// <summary>Live raids and visits (sub-project 3b spec §6).</summary>
public sealed class LiveOptions
{
    public const string Section = "Live";

    public bool Enabled { get; set; } = true;
    public int InviteSeconds { get; set; } = 60;
    public int ReconnectWaitMinutes { get; set; } = 5;
    public int GuestPauses { get; set; } = 10;
    public int GuestPauseSeconds { get; set; } = 60;
    public int MaxFrameBytes { get; set; } = 262144;
    public int DeltasPerSecond { get; set; } = 20;
    public int OrdersPerSecond { get; set; } = 30;
    /// <summary>A live stream without any frame for this long counts as lost (tests lower it).</summary>
    public int HeartbeatTimeoutSeconds { get; set; } = 10;
    /// <summary>How often invitations, waits and heartbeats are checked.</summary>
    public int SweepSeconds { get; set; } = 5;
}

public sealed class AdminOptions
{
    public const string Section = "Admin";

    public string? ApiToken { get; set; }
    /// <summary>Named, scoped tokens for integrations (Discord bot, scripts); see <c>Rest/ApiTokens</c>.</summary>
    public List<ApiTokenOptions> Tokens { get; set; } = [];
    /// <summary>Events the admin feed keeps for subscribers resuming by <c>Last-Event-ID</c> (100–100000).</summary>
    public int FeedBufferSize { get; set; } = 1000;
    /// <summary>Seconds without an event before the feed sends a <c>ping</c> (1–300).</summary>
    public int FeedPingSeconds { get; set; } = 15;
}

public sealed class ApiTokenOptions
{
    public string Name { get; set; } = "";
    public string Token { get; set; } = "";
    public string[] Scopes { get; set; } = [];
}

public enum DevModePolicy { AdminsOnly, Everyone }

public enum StorytellerPolicy { Locked, Free }

/// <summary>Integrity rules (integrity spec §1): who may use dev mode, wealth-jump thresholds, the world's storyteller for every colony.</summary>
public sealed class IntegrityOptions
{
    public const string Section = "Integrity";

    public DevModePolicy DevMode { get; set; } = DevModePolicy.AdminsOnly;
    /// <summary>Net wealth gain per game day that alerts admins; 0 disables.</summary>
    public long WealthWarnPerDay { get; set; } = 20000;
    /// <summary>Net wealth gain per game day that also freezes the player's economy; 0 disables.</summary>
    public long WealthFreezePerDay { get; set; } = 100000;
    public StorytellerPolicy StorytellerPolicy { get; set; } = StorytellerPolicy.Locked;

    public static bool IsValid(IntegrityOptions o) =>
        o.WealthWarnPerDay >= 0 && o.WealthFreezePerDay >= 0
        && (o.WealthWarnPerDay == 0 || o.WealthFreezePerDay == 0 || o.WealthFreezePerDay >= o.WealthWarnPerDay);

    public bool DevModeAllowed(Storage.Entities.Role role) => DevMode == DevModePolicy.Everyone || role == Storage.Entities.Role.Admin;
}
