using Rimlink.Server.Storage.Entities;

namespace Rimlink.Server.Application.Sessions;

/// <summary>A logged-in connection. Cancelling <see cref="Cts"/> ends its event stream.</summary>
public sealed class Session
{
    public required string Token { get; init; }
    public required long AccountId { get; init; }
    public required long PlayerId { get; init; }
    public required Role Role { get; init; }
    public required string Ip { get; init; }
    public required string PlayerName { get; init; }
    public CancellationTokenSource Cts { get; } = new();
    public Events.EventQueue Events { get; } = new();
    public DateTime StartedAt { get; } = DateTime.UtcNow;
    /// <summary>Where the "while you were away" summary of this session starts; null on the account's first login.</summary>
    public DateTime? AwaySince { get; set; }
    /// <summary>What happened to the player's archived colony at this login, if they had one.</summary>
    public Protocol.V1.ColonyRestore? ColonyRestore { get; set; }
    /// <summary>Presence for live sessions: the client reports whether the player is on their home map.</summary>
    public volatile bool AtHome;
}
