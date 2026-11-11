using Rimlink.Server.Application.GuildCapture;
using Xunit;

namespace Rimlink.Server.Tests.GuildCapture;

public sealed class GuildBattleScheduleTests
{
    private static readonly DateTime Now = new(2030, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(12)]
    [InlineData(23)]
    public void The_entire_thirty_minute_reservation_must_fit_the_defence_window(int hour)
    {
        DateTime window = Now.Date.AddDays(2).AddHours(hour);
        Assert.Equal(window.AddMinutes(90), GuildBattleSchedule.NextEligibleSlot(Now, hour, Now, [window.AddMinutes(90)]));
        Assert.Null(GuildBattleSchedule.NextEligibleSlot(Now, hour, Now, [window.AddMinutes(90).AddTicks(1), window.AddMinutes(119)]));
    }

    [Fact]
    public void Notice_and_seven_day_horizon_are_inclusive_but_window_end_is_not()
    {
        DateTime first = Now.AddHours(24), last = Now.AddDays(7);
        Assert.Equal(first, GuildBattleSchedule.NextEligibleSlot(Now, 12, Now,
            [first.AddTicks(-1), first, first.AddHours(2), last.AddTicks(1)]));
        Assert.Equal(last, GuildBattleSchedule.NextEligibleSlot(Now, 12, Now, [last]));
        Assert.Null(GuildBattleSchedule.NextEligibleSlot(Now, 12, Now, [first.AddHours(2), last.AddTicks(1)]));
    }

    [Fact]
    public void Midnight_window_and_protection_are_checked_against_real_available_slots()
    {
        DateTime midnight = Now.Date.AddDays(2);
        Assert.Equal(midnight, GuildBattleSchedule.NextEligibleSlot(Now, 23, Now, [midnight.AddHours(1), midnight]));
        Assert.Null(GuildBattleSchedule.NextEligibleSlot(Now, 23, Now, []));
        Assert.Null(GuildBattleSchedule.NextEligibleSlot(Now, 23, midnight.AddTicks(1), [midnight]));
        Assert.Equal(midnight, GuildBattleSchedule.NextEligibleSlot(Now, 23, midnight, [midnight]));
    }

    [Fact]
    public void Seventy_two_hour_shield_or_cooldown_cannot_be_shortened_by_changing_owner()
    {
        DateTime protectedUntil = GuildBattleSchedule.ProtectedUntil(Now, Now.AddHours(1));
        Assert.Equal(Now.AddHours(73), protectedUntil);
        Assert.Equal(Now.AddHours(72), GuildBattleSchedule.ProtectedUntil(Now, null));
        Assert.Null(GuildBattleSchedule.NextEligibleSlot(Now, 12, protectedUntil, [Now.AddHours(72)]));
        Assert.Equal(Now.AddHours(73), GuildBattleSchedule.NextEligibleSlot(Now, 12, protectedUntil, [Now.AddHours(73)]));
    }

    [Fact]
    public void Window_change_has_a_full_seven_day_delay() =>
        Assert.Equal(Now.AddDays(7), GuildBattleSchedule.WindowChangeEffectiveAt(Now));

    [Theory]
    [InlineData(-1)]
    [InlineData(24)]
    public void Invalid_defence_hours_are_refused(int hour) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => GuildBattleSchedule.NextEligibleSlot(Now, hour, Now, []));

    [Fact]
    public void Non_utc_inputs_are_refused_instead_of_guessing_a_timezone()
    {
        var unspecified = DateTime.SpecifyKind(Now, DateTimeKind.Unspecified);
        Assert.Throws<ArgumentException>(() => GuildBattleSchedule.NextEligibleSlot(unspecified, 12, Now, []));
        Assert.Throws<ArgumentException>(() => GuildBattleSchedule.NextEligibleSlot(Now, 12, unspecified, []));
        Assert.Throws<ArgumentException>(() => GuildBattleSchedule.NextEligibleSlot(Now, 12, Now, [unspecified]));
    }

    [Theory]
    [InlineData(true, false, false, false, false, true)]
    [InlineData(false, false, false, false, false, false)]
    [InlineData(true, true, false, false, false, false)]
    [InlineData(true, false, true, false, false, false)]
    [InlineData(true, false, false, true, false, false)]
    [InlineData(true, false, false, false, true, false)]
    public void Attacker_requires_active_unprotected_nonpeaceful_unfrozen_debt_free_colony(
        bool active, bool peaceful, bool newbie, bool frozen, bool debt, bool allowed) =>
        Assert.Equal(allowed, GuildBattleSchedule.MayAttack(1, 2, active, peaceful, newbie, frozen, debt));

    [Fact]
    public void Self_attack_or_invalid_alliance_is_refused()
    {
        Assert.False(GuildBattleSchedule.MayAttack(1, 1, true, false, false, false, false));
        Assert.False(GuildBattleSchedule.MayAttack(0, 2, true, false, false, false, false));
        Assert.False(GuildBattleSchedule.MayAttack(1, 0, true, false, false, false, false));
    }
}
