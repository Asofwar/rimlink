namespace Rimlink.Server.Application.Procurement;

public sealed class ProcurementDelivery
{
    public long Id { get; set; }
    public long OrderId { get; set; }
    public long SupplierPlayerId { get; set; }
    public int Quantity { get; set; }
    public byte[] Items { get; set; } = [];
    public long Paid { get; set; }
    public DateTime CreatedAt { get; set; }
}
