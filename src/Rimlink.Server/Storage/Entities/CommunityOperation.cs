namespace Rimlink.Server.Storage.Entities;

public sealed class CommunityOperation
{
    public long PlayerId { get; set; }
    public string RequestId { get; set; } = "";
    public byte[] Intent { get; set; } = [];
    public byte[] Result { get; set; } = [];
}
