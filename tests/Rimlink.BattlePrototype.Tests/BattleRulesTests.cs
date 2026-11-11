using Rimlink.BattlePrototype;
using Xunit;

namespace Rimlink.BattlePrototype.Tests;

public class BattleRulesTests
{
    [Theory]
    [InlineData("../arbitrary")]
    [InlineData("execute")]
    [InlineData("")]
    public void FaultHooksHaveAnExactLocalAllowlist(string point) => Assert.Throws<InvalidDataException>(
        () => BattleFixture.FaultPath(Path.GetTempPath(), point));

    [Fact]
    public void UnarmedFaultDoesNothing()
    {
        string root = Path.Combine(Path.GetTempPath(), "unused-battle-" + Guid.NewGuid().ToString("N"));
        BattleFixture.CrashIfArmed(root, "after-final-durable");
        Assert.False(Directory.Exists(root));
    }
    [Theory]
    [InlineData("valid")]
    [InlineData("session")]
    [InlineData("savedata")]
    [InlineData("process")]
    [InlineData("hash")]
    public void BindingRequiresTheExactOwnedProcessAndNoPlayerSession(string change)
    {
        string stand = Path.Combine(Path.GetTempPath(), "capture-binding-test");
        var id = new BattleIdentity(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, new('a', 64), new('b', 64), new('c', 64), 1);
        var binding = new BattleBinding(BattleRole.SideA, "pipe", new('a', 64), id, stand, Path.Combine(stand, "game"),
            Path.Combine(stand, "savedata", "side-a"), new('d', 64), new('e', 64), new('f', 64));
        void Check() => BattleFixture.CheckBinding(binding, Path.Combine(BattleFixture.MatchRoot(binding), "side-a.json"),
            Path.Combine(binding.GameRoot, change == "process" ? "Other.exe" : "RimWorld.exe"),
            change == "savedata" ? Path.Combine(stand, "savedata", "side-b") : binding.SavedataRoot,
            change == "hash" ? new('a', 64) : binding.GameAssemblySha256, binding.ClientAssemblySha256, binding.CompanionAssemblySha256,
            change == "session");
        if (change == "valid") Check(); else Assert.Throws<InvalidDataException>(Check);
    }
    private static readonly BattleZone Zone = new(10, 10, 20, 20);
    private static BattlePawn A(int x = 15) => new("a", BattleSide.Attacker, x, 15);
    private static BattlePawn B(int x = 30) => new("b", BattleSide.Defender, x, 15);

    [Fact]
    public void ExactlyThreeThousandConsecutiveTicksWins()
    {
        var rules = new BattleRules(Zone);
        for (int tick = 1; tick <= 2999; tick++) rules.Step(tick, [A(), B()]);
        Assert.Equal(BattleOutcome.Ongoing, rules.State.Outcome);
        Assert.Equal(2999, rules.State.HeldTicks);
        Assert.Equal(BattleOutcome.AttackerWin, rules.Step(3000, [A(), B()]).Outcome);
        Assert.Equal(BattleEndReason.ZoneHeld, rules.State.Reason);
    }

    [Theory]
    [InlineData("contest")]
    [InlineData("empty")]
    [InlineData("downed")]
    [InlineData("mental")]
    [InlineData("incapable")]
    public void LosingControlResetsTheWholeConsecutiveCounter(string interruption)
    {
        var rules = new BattleRules(Zone);
        for (int tick = 1; tick <= 2999; tick++) rules.Step(tick, [A(), B()]);
        var attacker = interruption switch
        {
            "empty" => A(25), "downed" => A() with { Downed = true },
            "mental" => A() with { Mental = true }, "incapable" => A() with { CanFight = false }, _ => A()
        };
        // Another capable attacker keeps this a zone reset rather than elimination.
        rules.Step(3000, [attacker, A(30) with { Id = "a2" }, B(interruption == "contest" ? 15 : 30)]);
        Assert.Equal(0, rules.State.HeldTicks);
        Assert.Equal(BattleOutcome.Ongoing, rules.State.Outcome);
        Assert.Equal(1, rules.Step(3001, [A(), B()]).HeldTicks);
    }

    [Fact]
    public void MutualEliminationHasPriorityOverVictoryAndDeadline()
    {
        var rules = new BattleRules(Zone);
        for (int tick = 1; tick < 30000; tick++) rules.Step(tick, [A(30), B()]);
        var state = rules.Step(30000, [A() with { Alive = false }, B() with { Downed = true }]);
        Assert.Equal(BattleOutcome.Draw, state.Outcome);
        Assert.Equal(BattleEndReason.MutualElimination, state.Reason);
    }

    [Theory]
    [InlineData(BattleSide.Attacker, BattleOutcome.DefenderWin)]
    [InlineData(BattleSide.Defender, BattleOutcome.AttackerWin)]
    public void LastCombatantLostDeterminesWinner(BattleSide lost, BattleOutcome expected)
    {
        var rules = new BattleRules(Zone);
        var pawns = new[] { A(), B() }.Select(p => p.Side == lost ? p with { Mental = true } : p).ToArray();
        Assert.Equal(expected, rules.Step(1, pawns).Outcome);
    }

    [Fact]
    public void MatchUsesRelativeTicksAndDefenderKeepsObjectAtDeadline()
    {
        var rules = new BattleRules(Zone, 580);
        for (int relative = 1; relative < 30000; relative++) rules.Step(580 + relative, [A(30), B()]);
        Assert.Equal(BattleOutcome.Ongoing, rules.State.Outcome);
        var state = rules.Step(30580, [A(30), B()]);
        Assert.Equal(BattleOutcome.DefenderWin, state.Outcome);
        Assert.Equal(BattleEndReason.TickLimit, state.Reason);
    }

    [Fact]
    public void CompletingObjectiveOnTheLastTickStillWins()
    {
        var rules = new BattleRules(Zone);
        for (int tick = 1; tick <= 27000; tick++) rules.Step(tick, [A(30), B()]);
        for (int tick = 27001; tick <= 30000; tick++) rules.Step(tick, [A(), B()]);
        Assert.Equal(BattleOutcome.AttackerWin, rules.State.Outcome);
    }

    [Fact]
    public void WallClockAbortDoesNotAwardAStalledMatchToDefender()
    {
        var rules = new BattleRules(Zone);
        Assert.Equal(BattleOutcome.Ongoing, rules.CheckWallClock(TimeSpan.FromMinutes(20) - TimeSpan.FromTicks(1)).Outcome);
        var state = rules.CheckWallClock(TimeSpan.FromMinutes(20));
        Assert.Equal(0, state.Tick);
        Assert.Equal(BattleOutcome.TechnicalAbort, state.Outcome);
        Assert.Equal(BattleEndReason.WallClockLimit, state.Reason);
    }

    [Fact]
    public void SkippedOrRepeatedTickIsNotInventedSimulation()
    {
        var rules = new BattleRules(Zone);
        Assert.Throws<InvalidOperationException>(() => rules.Step(2, [A(), B()]));
        rules.Step(1, [A(), B()]);
        Assert.Throws<InvalidOperationException>(() => rules.Step(1, [A(), B()]));
        Assert.Equal(1, rules.State.Tick);
    }

    [Fact]
    public void TerminalResultCannotBeOverwrittenByAnotherTickOrClock()
    {
        var rules = new BattleRules(Zone);
        var won = rules.Step(1, [A(), B() with { Alive = false }]);
        Assert.Throws<InvalidOperationException>(() => rules.Step(2, [A() with { Alive = false }, B()]));
        Assert.Equal(won, rules.CheckWallClock(TimeSpan.FromHours(1)));
    }

    [Fact]
    public void DuplicateIdentityOrUnknownSideIsRejectedBeforeStateChanges()
    {
        var rules = new BattleRules(Zone);
        Assert.Throws<InvalidDataException>(() => rules.Step(1, [A(), B() with { Id = "a" }]));
        Assert.Throws<InvalidDataException>(() => rules.Step(1, [A(), B() with { Side = (BattleSide)9 }]));
        Assert.Equal(0, rules.State.Tick);
    }

    [Fact]
    public void InvalidZoneOrStartTickCannotCreateRules()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new BattleRules(new(20, 10, 10, 20)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BattleRules(Zone, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BattleRules(Zone, int.MaxValue));
    }

    [Fact]
    public void InvalidHealthOrMissingIdentityCannotMutateTheTick()
    {
        var rules = new BattleRules(Zone);
        Assert.Throws<InvalidDataException>(() => rules.Step(1, [A() with { Id = "" }, B()]));
        Assert.Throws<InvalidDataException>(() => rules.Step(1, [A() with { Health = float.NaN }, B()]));
        Assert.Throws<InvalidDataException>(() => rules.Step(1, [A() with { Health = 2 }, B()]));
        Assert.Equal(0, rules.State.Tick);
    }
}
