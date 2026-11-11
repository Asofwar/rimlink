namespace Rimlink.BattlePrototype;

public sealed class BattleRules
{
    public const int CaptureTicks = 3000;
    public const int MatchTicks = 30000;
    public static readonly TimeSpan WallClockLimit = TimeSpan.FromMinutes(20);
    private readonly BattleZone zone;
    private readonly int startTick;
    public BattleRuleState State { get; private set; }

    public BattleRules(BattleZone zone, int startTick = 0)
    {
        if (zone.MinX < 0 || zone.MinZ < 0 || zone.MaxX < zone.MinX || zone.MaxZ < zone.MinZ)
            throw new ArgumentOutOfRangeException(nameof(zone));
        if (startTick < 0 || startTick > int.MaxValue - MatchTicks)
            throw new ArgumentOutOfRangeException(nameof(startTick));
        this.zone = zone;
        this.startTick = startTick;
        State = new(startTick, 0, BattleOutcome.Ongoing, BattleEndReason.None);
    }

    public BattleRuleState Step(int tick, IReadOnlyList<BattlePawn> pawns)
    {
        if (State.Outcome != BattleOutcome.Ongoing || tick != State.Tick + 1)
            throw new InvalidOperationException("battle ticks must be consecutive and stop at the terminal tick");
        bool attacker = false, defender = false, attackerInZone = false, defenderInZone = false;
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pawn in pawns)
        {
            if (string.IsNullOrWhiteSpace(pawn.Id) || pawn.Id.Length > 64 || !ids.Add(pawn.Id)
                || !Enum.IsDefined(pawn.Side) || !float.IsFinite(pawn.Health) || pawn.Health is < 0 or > 1)
                throw new InvalidDataException("invalid battle pawn identity or state");
            if (!pawn.CombatReady) continue;
            if (pawn.Side == BattleSide.Attacker) { attacker = true; attackerInZone |= zone.Contains(pawn); }
            else { defender = true; defenderInZone |= zone.Contains(pawn); }
        }
        int held = attackerInZone && !defenderInZone ? State.HeldTicks + 1 : 0;
        var (outcome, reason) = (!attacker && !defender) ? (BattleOutcome.Draw, BattleEndReason.MutualElimination)
            : !defender ? (BattleOutcome.AttackerWin, BattleEndReason.SideEliminated)
            : !attacker ? (BattleOutcome.DefenderWin, BattleEndReason.SideEliminated)
            : held >= CaptureTicks ? (BattleOutcome.AttackerWin, BattleEndReason.ZoneHeld)
            : tick - startTick >= MatchTicks ? (BattleOutcome.DefenderWin, BattleEndReason.TickLimit)
            : (BattleOutcome.Ongoing, BattleEndReason.None);
        return State = new(tick, held, outcome, reason);
    }

    public BattleRuleState CheckWallClock(TimeSpan elapsed)
    {
        if (State.Outcome == BattleOutcome.Ongoing && elapsed >= WallClockLimit)
            State = State with { Outcome = BattleOutcome.TechnicalAbort, Reason = BattleEndReason.WallClockLimit };
        return State;
    }
}
