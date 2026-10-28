namespace Rimlink.Server.Storage.Entities;

public enum Role { Player = 0, Moderator = 1, Admin = 2 }

public enum MessageKind { Player = 0, System = 1 }

public enum EntryKind { Required = 0, Allowed = 1 }

public static class WorldObjectTypes
{
    public const string Settlement = "settlement";
}

public sealed class Account
{
    public long Id { get; set; }
    public long? SteamId { get; set; }
    public long? OwnerSteamId { get; set; }
    public string? Login { get; set; }
    public string? PasswordHash { get; set; }
    public Role Role { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? LastSeenAt { get; set; }

    public Player? Player { get; set; }
}

public sealed class Player
{
    public long Id { get; set; }
    public long AccountId { get; set; }
    public Account Account { get; set; } = null!;
    public string Name { get; set; } = "";
    public string FactionName { get; set; } = "";
    public uint ColorArgb { get; set; } = 0xFF4A90D9;
    public string? LastIp { get; set; }
    public DateTime? MutedUntil { get; set; }
    /// <summary>Tick of the save that accompanied the last outgoing transfer; older saves are refused (they would restore what was given away).</summary>
    public long LastOutgoingTick { get; set; }
}

public sealed class SessionLog
{
    public long Id { get; set; }
    public long AccountId { get; set; }
    public string Ip { get; set; } = "";
    public DateTime StartedAt { get; set; }
    public DateTime? EndedAt { get; set; }
}

public sealed class Ban
{
    public long Id { get; set; }
    public long? AccountId { get; set; }
    public long? SteamId { get; set; }
    public string? Ip { get; set; }
    public string Reason { get; set; } = "";
    public DateTime? ExpiresAt { get; set; }
    public long IssuedByAccountId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? PardonedAt { get; set; }
}

/// <summary>Single row: the shared planet's generation parameters.</summary>
public sealed class World
{
    public long Id { get; set; }
    public string Seed { get; set; } = "";
    public byte[] Params { get; set; } = [];
    /// <summary>Comma separated tile ids occupied by NPC settlements.</summary>
    public string NpcTiles { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}

public sealed class WorldObject
{
    public long Id { get; set; }
    public string Type { get; set; } = WorldObjectTypes.Settlement;
    public long OwnerPlayerId { get; set; }
    public Player Owner { get; set; } = null!;
    public int Tile { get; set; }
    public string Name { get; set; } = "";
    public byte[] Payload { get; set; } = [];
    public DateTime CreatedAt { get; set; }
}

public sealed class ChatMessage
{
    public long Id { get; set; }
    public long? FromPlayerId { get; set; }
    public long? ToPlayerId { get; set; }
    public string Text { get; set; } = "";
    public MessageKind Kind { get; set; }
    public DateTime SentAt { get; set; }
}

public sealed class ModpackEntry
{
    public long Id { get; set; }
    public int Position { get; set; }
    public string PackageId { get; set; } = "";
    public string Version { get; set; } = "";
    public ulong? WorkshopId { get; set; }
    public string? Url { get; set; }
    public EntryKind Kind { get; set; }
}

public sealed class SaveVersion
{
    public long Id { get; set; }
    public long PlayerId { get; set; }
    public int Number { get; set; }
    public long GameTick { get; set; }
    public long Size { get; set; }
    public byte[] Sha256 { get; set; } = [];
    public DateTime CreatedAt { get; set; }
    public bool IsCurrent { get; set; }
}

public enum ParcelKindDb { Gift = 0, Purchase = 1, LotReturn = 2, Withdraw = 3, Admin = 4 }

public enum LotStatusDb { Open = 0, Sold = 1, Withdrawn = 2, Expired = 3 }

/// <summary>Silver balance per player; the bank (fees) is player 0. Always equals the sum of the player's <see cref="WalletEntry"/> rows.</summary>
public sealed class Wallet
{
    public long PlayerId { get; set; }
    public long Balance { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public sealed class WalletEntry
{
    public long Id { get; set; }
    public long PlayerId { get; set; }
    public long Delta { get; set; }
    public string Kind { get; set; } = "";
    public long RefId { get; set; }
    public DateTime At { get; set; }
}

/// <summary>Items, pawns and silver waiting for a player; delivered when their game spawns them.</summary>
public sealed class Parcel
{
    public long Id { get; set; }
    public long ToPlayerId { get; set; }
    public long? FromPlayerId { get; set; }
    public ParcelKindDb Kind { get; set; }
    public byte[] Items { get; set; } = [];
    public byte[] Pawns { get; set; } = [];
    public long Silver { get; set; }
    public int SizeBytes { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? DeliveredAt { get; set; }
    public long? DeliveredTick { get; set; }
    public string? ClientRequestId { get; set; }
}

public sealed class Lot
{
    public long Id { get; set; }
    public long SellerPlayerId { get; set; }
    public byte[] Items { get; set; } = [];
    public byte[]? Pawn { get; set; }
    public long Price { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public LotStatusDb Status { get; set; }
    public long? BuyerPlayerId { get; set; }
    public DateTime? ClosedAt { get; set; }
    public string? ClientRequestId { get; set; }
}

/// <summary>Ledger of everything that moved between players, with the sender's save tick.</summary>
public sealed class Transfer
{
    public long Id { get; set; }
    public long FromPlayerId { get; set; }
    public long? ToPlayerId { get; set; }
    public string Kind { get; set; } = "";
    public long GameTick { get; set; }
    public string Summary { get; set; } = "";
    public DateTime At { get; set; }
}

/// <summary>ThingDef/PawnKindDef names of the server modpack; empty table = blob def check off.</summary>
public sealed class ModpackDef
{
    public string DefName { get; set; } = "";
}

public sealed class AdminAction
{
    public long Id { get; set; }
    public long ActorAccountId { get; set; }
    public string Action { get; set; } = "";
    public long? TargetAccountId { get; set; }
    public string Details { get; set; } = "";
    public DateTime At { get; set; }
}
