using RimWorld;
using Rimlink.Protocol.V1;
using Verse;
using Verse.AI;

namespace Rimlink.Client.Game.GuildCapture;

internal static class BattleOrders
{
    internal static BattleCommandReason Apply(BattleScenario scenario,BattlePreparedManifestV1 start,BattleFighterSide side,BattleCommandV1 command)
    {
        using var permit=BattleGameContext.AllowExecutorOrders();
        if(!start.Entities.Any(e=>e.Side==side && e.FighterId==command.FighterId) || !scenario.Pawns.TryGetValue(command.FighterId,out var pawn))return BattleCommandReason.ControlRevoked;
        if(pawn.Dead || !pawn.Spawned || pawn.Downed || pawn.InMentalState)return BattleCommandReason.InvalidState;
        switch(command.OrderCase)
        {
            case BattleCommandV1.OrderOneofCase.Move:
                var cell=new IntVec3(command.Move.X,0,command.Move.Z);
                if(!cell.InBounds(scenario.Map) || !cell.Walkable(scenario.Map) || !pawn.CanReach(cell,PathEndMode.OnCell,Danger.Deadly))return BattleCommandReason.TargetUnavailable;
                var move=JobMaker.MakeJob(JobDefOf.Goto,cell);move.playerForced=true;
                return pawn.jobs.TryTakeOrderedJob(move,JobTag.Misc)?BattleCommandReason.None:BattleCommandReason.InvalidState;
            case BattleCommandV1.OrderOneofCase.AttackFighterId:
                if(!start.Entities.Any(e=>e.Side!=side && e.FighterId==command.AttackFighterId) || !scenario.Pawns.TryGetValue(command.AttackFighterId,out var target)
                    || !target.Spawned || target.Dead)return BattleCommandReason.TargetUnavailable;
                var verb=pawn.TryGetAttackVerb(target,allowManualCastWeapons:true);if(verb is null)return BattleCommandReason.InvalidState;
                var attack=JobMaker.MakeJob(JobDefOf.AttackStatic,target);attack.verbToUse=verb;attack.playerForced=true;
                return pawn.jobs.TryTakeOrderedJob(attack,JobTag.Misc)?BattleCommandReason.None:BattleCommandReason.InvalidState;
            case BattleCommandV1.OrderOneofCase.Draft:
                // RimWorld leaves the draft controller unset for both arena factions.
                pawn.drafter ??= new Pawn_DraftController(pawn);
                pawn.drafter.Drafted=command.Draft;if(!command.Draft)pawn.jobs.EndCurrentJob(JobCondition.InterruptForced);
                return BattleCommandReason.None;
            default:return BattleCommandReason.InvalidState;
        }
    }
    internal static void Fallback(BattleScenario scenario,BattlePreparedManifestV1 start,BattleFighterSide side)
    {
        var pawns=scenario.ReadPawns();
        foreach(var pawn in pawns.Where(p=>(int)p.Side==(int)side && p.CombatReady))
        {
            var enemy=pawns.Where(p=>p.Side!=pawn.Side && p.CombatReady).OrderBy(p=>(long)(p.X-pawn.X)*(p.X-pawn.X)+(long)(p.Z-pawn.Z)*(p.Z-pawn.Z)).ThenBy(p=>p.Id,StringComparer.Ordinal).FirstOrDefault();
            if(enemy is null)continue;
            var native=scenario.Pawns[pawn.Id];
            if((long)(enemy.X-pawn.X)*(enemy.X-pawn.X)+(long)(enemy.Z-pawn.Z)*(enemy.Z-pawn.Z)<=900)
            {
                if(native.CurJob is {def:var job,targetA.Thing:Pawn target} && job==JobDefOf.AttackStatic && ReferenceEquals(target,scenario.Pawns[enemy.Id]))continue;
                Apply(scenario,start,side,new(){Version=1,FighterId=pawn.Id,AttackFighterId=enemy.Id});
            }
            else Apply(scenario,start,side,new(){Version=1,FighterId=pawn.Id,Move=new(){X=(start.Arena.ZoneMinX+start.Arena.ZoneMaxX)/2,Z=(start.Arena.ZoneMinZ+start.Arena.ZoneMaxZ)/2}});
        }
    }
}
