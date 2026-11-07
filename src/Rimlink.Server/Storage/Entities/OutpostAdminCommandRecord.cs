namespace Rimlink.Server.Storage.Entities;

/// <summary>Immutable command receipt and the exact legacy audit projection; committed with its domain effect.</summary>
public sealed class OutpostAdminCommandRecord
{
    public string Id { get; set; } = "";
    public string ActorKey { get; set; } = "";
    public string Action { get; set; } = "";
    public string RequestId { get; set; } = "";
    public string Reason { get; set; } = "";
    public byte[] Command { get; set; } = [];
    public byte[] CommandDigest { get; set; } = [];
    public string TransitionId { get; set; } = "";
    public string NextHeadId { get; set; } = "";
    public byte[] Result { get; set; } = [];
    public long AuditId { get; set; }
    public byte[] AuditProjection { get; set; } = [];
    public DateTime CreatedAt { get; set; }
}
