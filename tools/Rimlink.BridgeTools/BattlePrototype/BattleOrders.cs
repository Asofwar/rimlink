using RimWorld;
using Rimlink.BattlePrototype;
using Verse;
using Verse.AI;

namespace Rimlink.BridgeTools.BattlePrototype;

internal static class BattleOrders
{
    internal static string? Apply(BattleScenario scenario, BattleSide side, BattleCommand command)
    {
        if (!scenario.Pawns.TryGetValue(command.PawnId, out var pawn) || !Owns(side, command.PawnId)) return "not your fighter";
        if (pawn.Dead || !pawn.Spawned || pawn.Downed || pawn.InMentalState) return "fighter cannot receive orders";
        switch (command.Kind)
        {
            case BattleCommandKind.MoveTo:
                var cell = new IntVec3(command.X, 0, command.Z);
                if (command.TargetPawnId is not null || !BattleFixture.Arena.Contains(new("", side, cell.x, cell.z))
                    || !cell.Walkable(scenario.Map) || !pawn.CanReach(cell, PathEndMode.OnCell, Danger.Deadly)) return "unreachable arena cell";
                var move = JobMaker.MakeJob(JobDefOf.Goto, cell); move.playerForced = true;
                return pawn.jobs.TryTakeOrderedJob(move, JobTag.Misc) ? null : "move refused";
            case BattleCommandKind.Attack:
                if (command.TargetPawnId is null || Owns(side, command.TargetPawnId)
                    || !scenario.Pawns.TryGetValue(command.TargetPawnId, out var target) || !target.Spawned || target.Dead) return "invalid enemy target";
                var verb = pawn.TryGetAttackVerb(target, allowManualCastWeapons: true);
                if (verb is null) return "no attack verb";
                var attack = JobMaker.MakeJob(JobDefOf.AttackStatic, target); attack.verbToUse = verb; attack.playerForced = true;
                return pawn.jobs.TryTakeOrderedJob(attack, JobTag.Misc) ? null : "attack refused";
            case BattleCommandKind.Draft:
                if (pawn.drafter is not null) pawn.drafter.Drafted = command.Drafted;
                if (!command.Drafted) pawn.jobs.EndCurrentJob(JobCondition.InterruptForced);
                return null;
            default: return "unknown battle command";
        }
    }

    internal static bool Owns(BattleSide side, string id) => BattleFixture.Roster().Any(p => p.Id == id && p.Side == side);

    internal static void Fallback(BattleScenario scenario, BattleSide side)
    {
        var roster = scenario.ReadPawns();
        foreach (var pawn in roster.Where(p => p.Side == side && p.CombatReady))
        {
            var enemy = roster.Where(p => p.Side != side && p.CombatReady)
                .OrderBy(p => (p.X - pawn.X) * (p.X - pawn.X) + (p.Z - pawn.Z) * (p.Z - pawn.Z)).FirstOrDefault();
            if (enemy is null) continue;
            if ((enemy.X - pawn.X) * (enemy.X - pawn.X) + (enemy.Z - pawn.Z) * (enemy.Z - pawn.Z) <= 900)
            {
                if (scenario.Pawns[pawn.Id].CurJob is { def: var job, targetA.Thing: Pawn target }
                    && job == JobDefOf.AttackStatic && ReferenceEquals(target, scenario.Pawns[enemy.Id])) continue;
                Apply(scenario, side, new(1, BattleCommandKind.Attack, pawn.Id, TargetPawnId: enemy.Id));
            }
            else Apply(scenario, side, new(1, BattleCommandKind.MoveTo, pawn.Id, 124, 124));
        }
    }
}
