using Rimlink.Protocol.V1;
namespace Rimlink.Server.Application.Procurement;

public sealed class ProcurementOrder
{
    public long Id { get; set; }
    public long BuyerPlayerId { get; set; }
    public long? SupplierPlayerId { get; set; }
    public ProcurementKind Kind { get; set; }
    public string DefName { get; set; } = "";
    public int Quantity { get; set; }
    public int Supplied { get; set; }
    public long Reward { get; set; }
    public long Escrow { get; set; }
    public ProcurementStatus Status { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
}
