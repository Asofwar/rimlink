namespace Rimlink.Server.Application.Procurement;

// Kept after closure: a lost response must never turn an already accepted delivery into a client-side return.
public sealed class ProcurementRequest
{
    public long Id { get; set; }
    public long PlayerId { get; set; }
    public string RequestId { get; set; } = "";
    public string IntentHash { get; set; } = "";
    public long OrderId { get; set; }
}
