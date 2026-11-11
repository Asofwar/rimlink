using Rimlink.Client.GuildCapture;
using Xunit;

namespace Rimlink.Client.Tests.GuildCapture;

public sealed class BattleRulesTests
{
    private static readonly BattleZone Zone = new(10, 10, 20, 20);
    private static BattlePawn[] Pawns() => Enumerable.Range(0, 8).Select(i => new BattlePawn("fighter" + i,
        i < 4 ? BattleSide.Attacker : BattleSide.Defender, i < 4 ? 15 : 30, 15, true, false, false, true, 1)).ToArray();
    private static BattleRules Rules(int start = 0) => new(Zone, Pawns().Select(p => new BattleFighter(p.Id, p.Side)).ToArray(), start);

    [Fact]
    public void Capture_needs_exactly_3000_consecutive_simulated_ticks()
    {
        var rules = Rules(); var pawns = Pawns();
        for (int tick = 1; tick < 3000; tick++) rules.Step(tick, pawns);
        Assert.Equal(BattleOutcome.Ongoing, rules.State.Outcome);
        Assert.Equal(2999, rules.State.HeldTicks);
        var terminal = rules.Step(3000, pawns);
        Assert.Equal(BattleOutcome.AttackerWin, terminal.Outcome);
        Assert.Equal(BattleEndReason.ZoneHeld, terminal.Reason);
        Assert.Throws<InvalidOperationException>(() => rules.Step(3001, pawns));
        Assert.Equal(terminal, rules.CheckWallClock(TimeSpan.FromHours(1)));
    }

    [Theory]
    [InlineData("contested")]
    [InlineData("empty")]
    [InlineData("incapable")]
    public void Lost_control_resets_the_counter(string interruption)
    {
        var rules = Rules(); var pawns = Pawns();
        for (int tick = 1; tick < 3000; tick++) rules.Step(tick, pawns);
        var interrupted = pawns.Select(p => interruption switch {
            "contested" when p.Id == "fighter4" => p with { X = 15 },
            "empty" when p.Side == BattleSide.Attacker => p with { X = 30 },
            "incapable" when p.Side == BattleSide.Attacker => p with { Downed = true }, _ => p }).ToArray();
        if (interruption == "incapable") interrupted[3] = interrupted[3] with { Downed = false, X = 30 };
        Assert.Equal(0, rules.Step(3000, interrupted).HeldTicks);
        Assert.Equal(BattleOutcome.Ongoing, rules.State.Outcome);
        Assert.Equal(1, rules.Step(3001, pawns).HeldTicks);
    }

    [Theory]
    [InlineData(BattleSide.Attacker, BattleOutcome.DefenderWin)]
    [InlineData(BattleSide.Defender, BattleOutcome.AttackerWin)]
    public void Elimination_requires_every_fixed_fighter_on_the_side_to_be_unable_to_fight(BattleSide side, BattleOutcome winner)
    {
        var rules = Rules(); var pawns = Pawns().Select(p => p.Side == side ? p with { Mental = true } : p).ToArray();
        Assert.Equal(winner, rules.Step(1, pawns).Outcome);
        Assert.Equal(BattleEndReason.SideEliminated, rules.State.Reason);
    }

    [Fact]
    public void Mutual_elimination_is_a_draw_even_at_the_tick_limit()
    {
        var rules = Rules(580); var pawns = Pawns().Select(p => p with { X = 30 }).ToArray();
        for (int tick = 581; tick < 30580; tick++) rules.Step(tick, pawns);
        var terminal = rules.Step(30580, pawns.Select(p => p with { Alive = false }).ToArray());
        Assert.Equal(BattleOutcome.Draw, terminal.Outcome);
        Assert.Equal(BattleEndReason.MutualElimination, terminal.Reason);
    }

    [Theory]
    [InlineData(false, BattleOutcome.DefenderWin, BattleEndReason.TickLimit)]
    [InlineData(true, BattleOutcome.AttackerWin, BattleEndReason.ZoneHeld)]
    public void Deadline_uses_relative_ticks_and_a_completed_objective_on_the_last_tick_wins(bool lastCapture, BattleOutcome outcome, BattleEndReason reason)
    {
        var rules = Rules(580); var outside = Pawns().Select(p => p with { X = 30 }).ToArray();
        for (int relative = 1; relative <= 30000; relative++) rules.Step(580 + relative, lastCapture && relative > 27000 ? Pawns() : outside);
        Assert.Equal(outcome, rules.State.Outcome); Assert.Equal(reason, rules.State.Reason);
    }

    [Fact]
    public void Wall_clock_abort_never_invents_simulation_or_awards_the_object()
    {
        var rules = Rules();
        Assert.Equal(BattleOutcome.Ongoing, rules.CheckWallClock(TimeSpan.FromMinutes(20) - TimeSpan.FromTicks(1)).Outcome);
        Assert.Equal(BattleOutcome.TechnicalAbort, rules.CheckWallClock(TimeSpan.FromMinutes(20)).Outcome);
        Assert.Equal(0, rules.State.Tick); Assert.Equal(BattleEndReason.WallClockLimit, rules.State.Reason);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("extra")]
    [InlineData("duplicate")]
    [InlineData("side")]
    [InlineData("health")]
    [InlineData("unknown")]
    public void A_partial_or_foreign_snapshot_cannot_become_a_gameplay_defeat(string damage)
    {
        var rules = Rules(); var pawns = Pawns();
        switch (damage) {
            case "missing": pawns = pawns[..7]; break;
            case "extra": pawns = [..pawns, pawns[0] with { Id = "extra" }]; break;
            case "duplicate": pawns[7] = pawns[0]; break;
            case "side": pawns[0] = pawns[0] with { Side = BattleSide.Defender }; break;
            case "health": pawns[0] = pawns[0] with { Health = float.NaN }; break;
            case "unknown": pawns[0] = pawns[0] with { Id = "other" }; break;
        }
        Assert.Throws<InvalidDataException>(() => rules.Step(1, pawns));
        Assert.Equal(0, rules.State.Tick); Assert.Equal(0, rules.State.HeldTicks);
    }

    [Fact]
    public void Roster_is_frozen_and_duplicate_or_skipped_ticks_do_not_advance_the_state()
    {
        var roster = Pawns().Select(p => new BattleFighter(p.Id, p.Side)).ToArray(); var rules = new BattleRules(Zone, roster);
        roster[0] = new("changed", BattleSide.Defender);
        Assert.Throws<InvalidOperationException>(() => rules.Step(2, Pawns()));
        rules.Step(1, Pawns()); Assert.Throws<InvalidOperationException>(() => rules.Step(1, Pawns()));
        Assert.Equal(1, rules.State.Tick);
        Assert.Throws<InvalidDataException>(() => new BattleRules(Zone, roster));
        Assert.Throws<ArgumentOutOfRangeException>(() => Rules(int.MaxValue));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BattleRules(new(20,10,10,20), Pawns().Select(p => new BattleFighter(p.Id,p.Side)).ToArray()));
    }
}
