using Prometheus;

namespace Rimlink.Server.Application.World;

/// <summary>Prometheus counters for the colony archive; scraped at /metrics with the rest.</summary>
public static class HygieneMetrics
{
    public static readonly Counter Archived = Metrics.CreateCounter("rimlink_colonies_archived_total", "Colonies moved to the archive, by reason (auto, admin, failed)", "reason");
    public static readonly Counter Restored = Metrics.CreateCounter("rimlink_colonies_restored_total", "Colonies brought back from the archive, by who (player, admin)", "by");
}
