namespace Rimlink.Server.Storage.Entities;

public sealed class AllianceAudit
{
    public long Id { get; set; }
    public long AllianceId { get; set; }
    public long ActorId { get; set; }
    public long SubjectId { get; set; }
    public string Kind { get; set; } = "";
    public long Delta { get; set; }
    public long Balance { get; set; }
    public long DailyCap { get; set; }
    public bool MayWithdraw { get; set; }
    public DateTime At { get; set; }
}
