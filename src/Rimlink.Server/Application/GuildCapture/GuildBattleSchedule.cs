namespace Rimlink.Server.Application.GuildCapture;

public static class GuildBattleSchedule
{
    public static readonly TimeSpan ReservationDuration = TimeSpan.FromMinutes(30);
    public static DateTime? NextEligibleSlot(DateTime nowUtc, int defenceHourUtc, DateTime protectedUntilUtc,
        IReadOnlyList<DateTime> availableSlots)
    {
        Utc(nowUtc); Utc(protectedUntilUtc);
        if (defenceHourUtc is < 0 or > 23) throw new ArgumentOutOfRangeException(nameof(defenceHourUtc));
        ArgumentNullException.ThrowIfNull(availableSlots);
        DateTime first = nowUtc.AddHours(24), last = nowUtc.AddDays(7);
        DateTime? chosen = null;
        foreach (DateTime slot in availableSlots)
        {
            Utc(slot);
            long offset = (slot.TimeOfDay.Ticks - defenceHourUtc * TimeSpan.TicksPerHour + TimeSpan.TicksPerDay) % TimeSpan.TicksPerDay;
            if (slot >= first && slot <= last && slot >= protectedUntilUtc && offset <= 2 * TimeSpan.TicksPerHour - ReservationDuration.Ticks
                && (chosen is null || slot < chosen)) chosen = slot;
        }
        return chosen;
    }

    public static DateTime ProtectedUntil(DateTime acquiredAtUtc, DateTime? lastBattleAtUtc)
    {
        Utc(acquiredAtUtc);
        if (lastBattleAtUtc is { } last) Utc(last);
        return (lastBattleAtUtc > acquiredAtUtc ? lastBattleAtUtc.Value : acquiredAtUtc).AddHours(72);
    }

    public static DateTime WindowChangeEffectiveAt(DateTime requestedAtUtc)
    {
        Utc(requestedAtUtc);
        return requestedAtUtc.AddDays(7);
    }

    public static bool MayAttack(long attackerAllianceId, long defenderAllianceId, bool activeColony,
        bool peaceful, bool newbie, bool frozen, bool debt) =>
        attackerAllianceId > 0 && defenderAllianceId > 0 && attackerAllianceId != defenderAllianceId
        && activeColony && !peaceful && !newbie && !frozen && !debt;

    private static void Utc(DateTime value)
    {
        if (value.Kind != DateTimeKind.Utc) throw new ArgumentException("UTC coordinates are required.");
    }
}
