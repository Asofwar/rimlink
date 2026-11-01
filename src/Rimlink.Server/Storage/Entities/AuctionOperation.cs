namespace Rimlink.Server.Storage.Entities;

public sealed class AuctionOperation
{
    public long PlayerId { get; set; }
    public string RequestId { get; set; } = "";
    public string IntentHash { get; set; } = "";
    public byte[] Response { get; set; } = [];
}
