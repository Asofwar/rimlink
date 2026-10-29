using Rimlink.Server.Hosting;

namespace Rimlink.Server.Application.Incidents;

/// <summary>
/// What a paid incident costs (paid incidents spec §2): BasePrice × (W/100000)^(2/3), times L^(3/2) for the level of a hostile
/// event, times the entry's multiplier and the drop-pod multiplier; never below MinPrice, saturating at long.MaxValue.
/// </summary>
public static class IncidentPrice
{
    /// <summary>Levels 1–4 are 0.5×, 1×, 1.5×, 2× the target's usual threat points.</summary>
    public static double LevelFactor(int level) => level switch
    {
        1 => 0.5,
        2 => 1,
        3 => 1.5,
        4 => 2,
        _ => throw new ArgumentOutOfRangeException(nameof(level), level, "level is 1..4"),
    };

    public static bool IsHostile(IncidentCatalogEntry entry) => entry.Kind == "Hostile";

    public static long For(IncidentCatalogEntry entry, int level, bool pods, decimal wealth, IncidentOptions o)
    {
        double price = o.BasePrice * Math.Pow(Math.Max(0, (double)wealth) / 100000.0, 2.0 / 3.0) * entry.PriceMultiplier;
        if (IsHostile(entry)) price *= Math.Pow(LevelFactor(level), 1.5);
        if (pods && entry.PodsMultiplier is { } podsMultiplier) price *= podsMultiplier;
        if (double.IsNaN(price) || price >= long.MaxValue) return long.MaxValue;
        return Math.Max(o.MinPrice, (long)Math.Round(price, MidpointRounding.AwayFromZero));
    }
}
