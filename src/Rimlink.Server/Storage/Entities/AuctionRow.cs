namespace Rimlink.Server.Storage.Entities;

public sealed class AuctionRow
{
    public long Id { get; set; }
    public long SellerPlayerId { get; set; }
    public byte[] Items { get; set; } = [];
    public long StartPrice { get; set; }
    public long BuyoutPrice { get; set; }
    public long Bid { get; set; }
    public long? BidderPlayerId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime MaxExpiresAt { get; set; }
    public int Status { get; set; }
    public DateTime? ClosedAt { get; set; }
    public string CloseReason { get; set; } = "";
}
