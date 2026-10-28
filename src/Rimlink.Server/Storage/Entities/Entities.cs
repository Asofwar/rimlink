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
}

public sealed class Session
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

public sealed class AdminAction
{
    public long Id { get; set; }
    public long ActorAccountId { get; set; }
    public string Action { get; set; } = "";
    public long? TargetAccountId { get; set; }
    public string Details { get; set; } = "";
    public DateTime At { get; set; }
}
