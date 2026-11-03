using System.Globalization;
using System.Numerics;
using Rimlink.Protocol.V1;

namespace Rimlink.Server.Application.Outposts;

public sealed record ProductionState(int Stock, string FractionNumerator = "0", string FractionDenominator = "1");
public sealed record WorkerRate(OutpostProfile Profile, int Skill, float Health);
public sealed record ProductionResult(ProductionState State);

/// <summary>Exact, bounded real-time production. The caller atomically persists State and advances its clock.</summary>
public static class OutpostProduction
{
    // Every binary32 H is an integer multiple of 2^-149. Steel adds /2 and elapsed adds /ticksPerHour.
    // Fractions from any valid sequence therefore divide this fixed lattice, even across profile changes.
    private static readonly BigInteger Denominator = (BigInteger.One << 150) * TimeSpan.TicksPerHour;
    private const int MaximumDigits = 64;

    public static ProductionResult Settle(ProductionState state, IReadOnlyList<WorkerRate> workers, TimeSpan elapsed)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(workers);
        var numerator = ParseUnsigned(state.FractionNumerator);
        var denominator = ParseUnsigned(state.FractionDenominator);
        if (state.Stock is < 0 or > 1000 || denominator <= 0 || denominator > Denominator ||
            numerator >= denominator || BigInteger.GreatestCommonDivisor(numerator, denominator) != 1 ||
            Denominator % denominator != 0 || (state.Stock == 1000 && numerator != 0))
            throw new ArgumentException("Invalid production stock or noncanonical fraction.", nameof(state));
        if (workers.Count > 4) throw new ArgumentException("The worker roster exceeds its bound.", nameof(workers));

        BigInteger rate = 0;
        OutpostProfile? profile = null;
        foreach (var worker in workers)
        {
            if (worker is null || worker.Profile is not (OutpostProfile.Wood or OutpostProfile.Steel) ||
                (profile is not null && profile != worker.Profile) || !float.IsFinite(worker.Health) || worker.Health <= 0)
                throw new ArgumentException("Invalid worker production inputs.", nameof(workers));
            profile = worker.Profile;
            var health = Math.Clamp(worker.Health, 0, 1);
            var bits = BitConverter.SingleToUInt32Bits(health);
            var exponent = (int)((bits >> 23) & 0xff);
            var significand = bits & 0x7fffff;
            // Scaled by 2^149, including the smallest subnormal; no floating-point accumulation.
            BigInteger scaledHealth = exponent == 0 ? significand : (BigInteger)(significand | 0x800000) << (exponent - 1);
            rate += (10 + Math.Clamp(worker.Skill, 0, 20)) * (worker.Profile == OutpostProfile.Wood ? 2 : 1) * scaledHealth;
        }

        if (state.Stock == 1000 || rate == 0 || elapsed.Ticks <= 0) return new(state);
        var quantity = state.Stock * Denominator + numerator * (Denominator / denominator);
        var remaining = 1000 * Denominator - quantity;
        // Compare with time to saturation before multiplying a potentially huge downtime.
        if (elapsed.Ticks >= (remaining + rate - 1) / rate) return new(new(1000));
        quantity += rate * elapsed.Ticks;
        var stock = (int)BigInteger.DivRem(quantity, Denominator, out var fraction);
        var divisor = BigInteger.GreatestCommonDivisor(fraction, Denominator);
        return new(new(stock, (fraction / divisor).ToString(CultureInfo.InvariantCulture),
            (Denominator / divisor).ToString(CultureInfo.InvariantCulture)));
    }

    private static BigInteger ParseUnsigned(string text)
    {
        if (string.IsNullOrEmpty(text) || text.Length > MaximumDigits || (text.Length > 1 && text[0] == '0') ||
            text.Any(c => c is < '0' or > '9') ||
            !BigInteger.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
            throw new ArgumentException("Invalid bounded canonical production integer.", nameof(text));
        return value;
    }
}
