namespace Rimlink.Server.Storage.Entities;

public sealed class InfluenceOperation
{
    public string Actor { get; set; } = "";
    public string RequestId { get; set; } = "";
    public byte[] Intent { get; set; } = [];
    public byte[] Result { get; set; } = [];
}
