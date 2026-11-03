using System.Text;
using Rimlink.Protocol.V1;

namespace Rimlink.Protocol;

public static class InfluenceValidation
{
    public static bool IsValid(InfluenceSnapshot snapshot)
    {
        if (!Guid.TryParseExact(snapshot.WorldInstanceId, "N", out _) || !Guid.TryParseExact(snapshot.ProcessInstanceId, "N", out _)
            || snapshot.Revision < 0 || snapshot.AlgorithmVersion != InfluenceLimits.AlgorithmVersion
            || snapshot.Sources.Count > InfluenceLimits.MaxSources || snapshot.Alliances.Count > InfluenceLimits.MaxSources
            || snapshot.CalculateSize() > InfluenceLimits.MaxSnapshotBytes) return false;
        var groups = new HashSet<long>();
        foreach (var group in snapshot.Alliances)
            if (group.Id <= 0 || !groups.Add(group.Id) || group.PaletteIndex is < 0 or >= InfluenceLimits.PaletteSize
                || Encoding.UTF8.GetByteCount(group.Name) is < 1 or > InfluenceLimits.MaxNameBytes
                || group.Name.Any(c => char.IsControl(c) || c is '<' or '>')) return false;
        var sources = new HashSet<long>();
        var tiles = new HashSet<int>();
        var used = new HashSet<long>();
        foreach (var source in snapshot.Sources)
        {
            if (source.Id <= 0 || !sources.Add(source.Id) || source.Tile < 0 || !tiles.Add(source.Tile)
                || source.Radius != InfluenceLimits.Radius || !groups.Contains(source.AllianceId)) return false;
            used.Add(source.AllianceId);
        }
        return groups.SetEquals(used);
    }
}
