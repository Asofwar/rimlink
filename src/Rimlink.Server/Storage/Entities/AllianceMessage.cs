namespace Rimlink.Server.Storage.Entities;

public sealed class AllianceMessage
{
    public long Id { get; set; }
    public long AllianceId { get; set; }
    public long FromId { get; set; }
    public string Text { get; set; } = "";
    public DateTime At { get; set; }
}
