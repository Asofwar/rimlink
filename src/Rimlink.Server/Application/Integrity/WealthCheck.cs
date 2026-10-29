using Rimlink.Server.Hosting;

namespace Rimlink.Server.Application.Integrity;

public enum WealthVerdict { None, Warn, Freeze }

/// <summary>Net wealth gain between two saves (silver), the game days between them (at least 1) and the gain per day.</summary>
public sealed record WealthResult(WealthVerdict Verdict, long Net, double Days, long Rate);

/// <summary>
/// The wealth-jump check (integrity spec §4): net gain = new wealth − previous wealth − what arrived by parcel in between, per
/// game day, against the two thresholds. Wealth is what the client reports; this catches spawning by an honest client.
/// </summary>
public static class WealthCheck
{
    public const int TicksPerDay = 60000;

    /// <summary>Null when there is nothing to compare: no previous wealth, or the save is not later than the previous one.</summary>
    public static WealthResult? Evaluate(long baseWealth, long baseTick, long newWealth, long newTick, long imported, IntegrityOptions options, bool isAdmin)
    {
        if (baseWealth <= 0 || newTick <= baseTick) return null;
        long net = newWealth - baseWealth - imported;
        double days = Math.Max(1.0, (newTick - baseTick) / (double)TicksPerDay);
        long rate = (long)(net / days);
        var verdict = WealthVerdict.None;
        if (options.WealthFreezePerDay > 0 && rate >= options.WealthFreezePerDay) verdict = isAdmin ? WealthVerdict.Warn : WealthVerdict.Freeze;
        else if (options.WealthWarnPerDay > 0 && rate >= options.WealthWarnPerDay) verdict = WealthVerdict.Warn;
        return new WealthResult(verdict, net, days, rate);
    }
}
