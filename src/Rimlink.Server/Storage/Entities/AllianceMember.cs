namespace Rimlink.Server.Storage.Entities;

public sealed class AllianceMember
{
    public long PlayerId { get; set; }
    public long AllianceId { get; set; }
    public DateTime JoinedAt { get; set; }
    public bool MayWithdraw { get; set; }
    public long DailyCap { get; set; }
    public DateTime WithdrawalDay { get; set; }
    public long Withdrawn { get; set; }
}
