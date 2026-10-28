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
}
