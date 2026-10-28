namespace Rimlink.Server.Application.Raids;

/// <summary>Admin switch for raids only (visits and the shop keep working); Raids:Enabled=false behaves the same.</summary>
public sealed class RaidFreezeState
{
    public bool Frozen { get; set; }
    public string? Reason { get; set; }
}
