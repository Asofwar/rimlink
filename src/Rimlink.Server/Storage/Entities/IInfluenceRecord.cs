namespace Rimlink.Server.Storage.Entities;

/// <summary>The same publication state follows a colony into and out of its archive.</summary>
public interface IInfluenceRecord
{
    bool InfluenceEnabled { get; set; }
    long InfluenceVersion { get; set; }
    long InfluenceAllianceId { get; set; }
    bool? InfluenceRootSurface { get; set; }
    string InfluenceWorldId { get; set; }
    long InfluenceSaveGeneration { get; set; }
    int InfluenceTile { get; set; }
}
