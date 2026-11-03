namespace Rimlink.Protocol;

public static class InfluenceLimits
{
    public const int AlgorithmVersion = 1;
    public const int Radius = 3;
    public const int MaxSources = 128;
    public const int MaxSnapshotBytes = 65_536;
    public const int MaxJsonBytes = 131_072;
    public const int MaxMutationBytes = 2_048;
    public const int MaxNameBytes = 128;
    public const int PaletteSize = 16;
    public const int MaxVisitedTiles = 23_936;
    public const int MaxNeighborChecks = 143_616;
}
