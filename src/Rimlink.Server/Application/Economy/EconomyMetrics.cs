using Prometheus;
using Rimlink.Protocol.V1;

namespace Rimlink.Server.Application.Economy;

/// <summary>Prometheus counters for the economy; scraped at /metrics with the rest.</summary>
public static class EconomyMetrics
{
    public static readonly Counter Trades = Metrics.CreateCounter("rimlink_trades_total", "Economy operations by kind", "kind");
    public static readonly Counter SilverMoved = Metrics.CreateCounter("rimlink_silver_moved_total", "Silver moved between wallets and parcels");
    public static readonly Gauge ParcelsPending = Metrics.CreateGauge("rimlink_parcels_pending", "Parcels not yet delivered");
    public static readonly Counter Errors = Metrics.CreateCounter("rimlink_economy_errors_total", "Refused economy operations by error code", "code");

    public static void Trade(string kind, long silver = 0)
    {
        Trades.WithLabels(kind).Inc();
        if (silver > 0) SilverMoved.Inc(silver);
    }

    public static void Error(ErrorCode code) => Errors.WithLabels(code.ToString()).Inc();
}
