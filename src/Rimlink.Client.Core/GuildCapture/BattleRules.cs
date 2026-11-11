namespace Rimlink.Client.GuildCapture;

public enum BattleSide { Attacker = 1, Defender = 2 }
public enum BattleOutcome { Ongoing, AttackerWin, DefenderWin, Draw, TechnicalAbort }
public enum BattleEndReason { None, ZoneHeld, SideEliminated, MutualElimination, TickLimit, WallClockLimit, TransportLost, InvalidState }
public sealed record BattleFighter(string Id, BattleSide Side);
public sealed record BattlePawn(string Id, BattleSide Side, int X, int Z, bool Alive, bool Downed, bool Mental, bool CanFight, float Health)
{
    public bool CombatReady => Alive && !Downed && !Mental && CanFight;
}
public sealed record BattleZone(int MinX, int MinZ, int MaxX, int MaxZ)
{
    public bool Contains(BattlePawn pawn) => pawn.X >= MinX && pawn.X <= MaxX && pawn.Z >= MinZ && pawn.Z <= MaxZ;
}
public sealed record BattleRuleState(int Tick, int HeldTicks, BattleOutcome Outcome, BattleEndReason Reason);

// Evaluated once after each native DoSingleTick; wall time never substitutes for simulation.
public sealed class BattleRules
{
    public const int CaptureTicks = 3000;
    public const int MatchTicks = 30000;
    public static readonly TimeSpan WallClockLimit = TimeSpan.FromMinutes(20);
    private readonly BattleZone zone;
    private readonly Dictionary<string, BattleSide> roster;
    private readonly int startTick;
    public BattleRuleState State { get; private set; }

    public BattleRules(BattleZone zone, IReadOnlyList<BattleFighter> fighters, int startTick = 0)
    {
        ArgumentNullException.ThrowIfNull(zone);
        ArgumentNullException.ThrowIfNull(fighters);
        if (zone.MinX < 0 || zone.MinZ < 0 || zone.MaxX < zone.MinX || zone.MaxZ < zone.MinZ) throw new ArgumentOutOfRangeException(nameof(zone));
        if (startTick < 0 || startTick > int.MaxValue - MatchTicks) throw new ArgumentOutOfRangeException(nameof(startTick));
        if (fighters.Count != 8 || fighters.Any(p => p is null || string.IsNullOrWhiteSpace(p.Id) || p.Id.Length > 64 || !Enum.IsDefined(p.Side))
            || fighters.Select(p => p.Id).Distinct(StringComparer.Ordinal).Count() != 8
            || fighters.Count(p => p.Side == BattleSide.Attacker) != 4 || fighters.Count(p => p.Side == BattleSide.Defender) != 4)
            throw new InvalidDataException("battle roster must contain exactly four fixed fighters per side");
        roster = fighters.ToDictionary(p => p.Id, p => p.Side, StringComparer.Ordinal);
        this.zone = zone; this.startTick = startTick;
        State = new(startTick, 0, BattleOutcome.Ongoing, BattleEndReason.None);
    }

    public BattleRuleState Step(int tick, IReadOnlyList<BattlePawn> pawns, TimeSpan? wallElapsed = null)
    {
        if (State.Outcome != BattleOutcome.Ongoing || tick != State.Tick + 1)
            throw new InvalidOperationException("battle ticks must be consecutive and stop at the terminal tick");
        ArgumentNullException.ThrowIfNull(pawns);
        if (wallElapsed < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(wallElapsed));
        if (pawns.Count != 8) throw new InvalidDataException("battle snapshot must include all eight fighters, including casualties");
        bool attacker = false, defender = false, attackerInZone = false, defenderInZone = false;
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pawn in pawns)
        {
            if (pawn is null || pawn.Id is null || !ids.Add(pawn.Id) || !roster.TryGetValue(pawn.Id, out var side) || side != pawn.Side
                || !float.IsFinite(pawn.Health) || pawn.Health is < 0 or > 1) throw new InvalidDataException("battle fighter identity or state differs from the fixed roster");
            if (!pawn.CombatReady) continue;
            if (side == BattleSide.Attacker) { attacker = true; attackerInZone |= zone.Contains(pawn); }
            else { defender = true; defenderInZone |= zone.Contains(pawn); }
        }
        int held = attackerInZone && !defenderInZone ? State.HeldTicks + 1 : 0;
        var (outcome, reason) = wallElapsed >= WallClockLimit ? (BattleOutcome.TechnicalAbort, BattleEndReason.WallClockLimit)
            : (!attacker && !defender) ? (BattleOutcome.Draw, BattleEndReason.MutualElimination)
            : !defender ? (BattleOutcome.AttackerWin, BattleEndReason.SideEliminated)
            : !attacker ? (BattleOutcome.DefenderWin, BattleEndReason.SideEliminated)
            : held >= CaptureTicks ? (BattleOutcome.AttackerWin, BattleEndReason.ZoneHeld)
            : tick - startTick >= MatchTicks ? (BattleOutcome.DefenderWin, BattleEndReason.TickLimit)
            : (BattleOutcome.Ongoing, BattleEndReason.None);
        return State = new(tick, held, outcome, reason);
    }

    public BattleRuleState Abort(BattleEndReason reason)
    {
        if (reason is not (BattleEndReason.TransportLost or BattleEndReason.InvalidState)) throw new ArgumentOutOfRangeException(nameof(reason));
        if (State.Outcome == BattleOutcome.Ongoing) State = State with { Outcome = BattleOutcome.TechnicalAbort, Reason = reason };
        return State;
    }

    public BattleRuleState CheckWallClock(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(elapsed));
        if (State.Outcome == BattleOutcome.Ongoing && elapsed >= WallClockLimit)
            State = State with { Outcome = BattleOutcome.TechnicalAbort, Reason = BattleEndReason.WallClockLimit };
        return State;
    }
}
