namespace Rimlink.Server.Application.GuildCapture;

public enum GuildResource { Steel = 1, Wood = 2 }
// Remainder is the fractional quantity scaled by TimeSpan.TicksPerHour.
public sealed record GuildStock(int Count, long Remainder = 0);

public static class GuildProduction
{
    public static GuildStock Settle(GuildResource resource, GuildStock stock, TimeSpan elapsed, bool paused = false)
    {
        int rate = resource switch
        {
            GuildResource.Steel => 10,
            GuildResource.Wood => 20,
            _ => throw new ArgumentOutOfRangeException(nameof(resource))
        };
        ArgumentNullException.ThrowIfNull(stock);
        if (stock.Count is < 0 or > 1000 || stock.Remainder is < 0 or >= TimeSpan.TicksPerHour
            || stock.Count == 1000 && stock.Remainder != 0)
            throw new ArgumentException("Invalid guild stock or fraction.", nameof(stock));
        if (paused || elapsed.Ticks <= 0 || stock.Count == 1000) return stock;
        long remaining = (1000L - stock.Count) * TimeSpan.TicksPerHour - stock.Remainder;
        // Saturate before multiplying even TimeSpan.MaxValue by the hourly rate.
        if (elapsed.Ticks >= (remaining + rate - 1) / rate) return new(1000);
        long quantity = stock.Remainder + elapsed.Ticks * rate;
        return new(stock.Count + (int)(quantity / TimeSpan.TicksPerHour), quantity % TimeSpan.TicksPerHour);
    }
}
