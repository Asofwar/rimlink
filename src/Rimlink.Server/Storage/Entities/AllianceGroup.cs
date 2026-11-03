namespace Rimlink.Server.Storage.Entities;

public sealed class AllianceGroup
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public long OwnerId { get; set; }
    public long Silver { get; set; }
    public bool Disbanded { get; set; }
    public int? PaletteIndex { get; set; }
}
