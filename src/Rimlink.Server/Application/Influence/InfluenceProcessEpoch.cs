namespace Rimlink.Server.Application.Influence;

public sealed class InfluenceProcessEpoch
{
    public string Id { get; } = Guid.NewGuid().ToString("N");
}
