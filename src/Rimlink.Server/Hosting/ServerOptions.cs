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
}

public sealed class ApiTokenOptions
{
    public string Name { get; set; } = "";
    public string Token { get; set; } = "";
    public string[] Scopes { get; set; } = [];
}
