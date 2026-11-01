namespace Rimlink.Server.Storage.Entities;

public sealed class CommunityInvitation
{
    public long Id { get; set; }
    public long FromId { get; set; }
    public long ToId { get; set; }
    public long AllianceId { get; set; }
    public string State { get; set; } = "pending";
    public DateTime ExpiresAt { get; set; }
}
