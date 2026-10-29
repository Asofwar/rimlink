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
    // Raids and visits (sub-project 3a).
    public bool Peaceful { get; set; }
    /// <summary>While set and in the future the player still counts as peaceful (switching off is delayed).</summary>
    public DateTime? PeacefulOffAt { get; set; }
    public bool VisitsClosed { get; set; }
    public DateTime? FirstSaveAt { get; set; }
    public long WealthTotal { get; set; }
    public long WealthItems { get; set; }
    /// <summary>Live sessions (3b): invitations are accepted without asking.</summary>
    public bool AutoAcceptLive { get; set; }
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

public enum ParcelKindDb { Gift = 0, Purchase = 1, LotReturn = 2, Withdraw = 3, Admin = 4, Loot = 5, RaidReturn = 6, Captive = 7 }

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
    /// <summary>Set for client-initiated operations without a parcel or lot of their own (deposits), to make repeats no-ops.</summary>
    public string? ClientRequestId { get; set; }
}

/// <summary>ThingDef/PawnKindDef names of the server modpack; empty table = blob def check off.</summary>
public sealed class ModpackDef
{
    public string DefName { get; set; } = "";
}

public enum RaidStateDb { Queued = 0, Announced = 1, Landed = 2, Resolved = 3, Expired = 4, Cancelled = 5 }

/// <summary>A raid waiting for, landed in or finished by the target's game; <see cref="Pawns"/> is the escrowed PawnList.</summary>
public sealed class Raid
{
    public long Id { get; set; }
    public long AttackerPlayerId { get; set; }
    public long TargetPlayerId { get; set; }
    public int PawnMode { get; set; }
    public int Timing { get; set; }
    public int Arrival { get; set; }
    public int Controller { get; set; }
    public RaidStateDb State { get; set; }
    public byte[] Pawns { get; set; } = [];
    public long LootCap { get; set; }
    public DateTime CreatedAt { get; set; }
    public long? AnnouncedTick { get; set; }
    public long? LandAtTick { get; set; }
    public long? LandedTick { get; set; }
    public DateTime? ResolvedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public string? SummaryJson { get; set; }
    public string? ClientRequestId { get; set; }
}

public sealed class RaidOutcome
{
    public long Id { get; set; }
    public long RaidId { get; set; }
    public int Index { get; set; }
    public int Outcome { get; set; }
}

public enum LiveStateDb { Proposed = 0, Starting = 1, Running = 2, Waiting = 3, Ending = 4, Ended = 5, Fallback = 6 }

/// <summary>A live raid or visit between an online host and guest (3b). A LiveRaid's escrow and rules live in its <see cref="Raid"/> row; a LiveVisit escrows its pawns here.</summary>
public sealed class LiveSession
{
    public long Id { get; set; }
    public int Kind { get; set; }
    public long HostPlayerId { get; set; }
    public long GuestPlayerId { get; set; }
    public LiveStateDb State { get; set; }
    public long? RaidId { get; set; }
    public byte[]? Pawns { get; set; }
    public int? SnapshotVersion { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? EndedAt { get; set; }
    public int? EndReason { get; set; }
    public string? ClientRequestId { get; set; }
    /// <summary>Set while Waiting: when the lost peer went missing, and who.</summary>
    public DateTime? WaitingSince { get; set; }
    public long? LostPlayerId { get; set; }
}

public sealed class Visit
{
    public long Id { get; set; }
    public long VisitorPlayerId { get; set; }
    public long HostPlayerId { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? EndedAt { get; set; }
}

/// <summary>A thing the host marked for sale; published from the host's save, sold from a visitor's copy of it.</summary>
public sealed class ShopItem
{
    public long Id { get; set; }
    public long HostPlayerId { get; set; }
    public long ThingId { get; set; }
    public string DefName { get; set; } = "";
    public string StuffDef { get; set; } = "";
    public int Count { get; set; }
    public int Quality { get; set; }
    public int HitPoints { get; set; }
    public long Price { get; set; }
    public long SaveTick { get; set; }
    public string Label { get; set; } = "";
    public DateTime? SoldAt { get; set; }
}

/// <summary>Sold things the host's game still has to take off its map.</summary>
public sealed class PendingRemoval
{
    public long Id { get; set; }
    public long HostPlayerId { get; set; }
    public long ThingId { get; set; }
    public int Count { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? AckedAt { get; set; }
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
