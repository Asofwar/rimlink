using RimWorld;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace Rimlink.Client.Game.Live;

/// <summary>
/// The lord of a guest's raiders while their player controls them: they stand where they are and do nothing on
/// their own (a lordless pawn of another faction walks off the map within seconds). The guest's orders run as
/// forced jobs on top of the idle duty.
/// </summary>
public sealed class LordJob_RimlinkGuests : LordJob
{
    public override bool AddFleeToil => false;

    public override StateGraph CreateGraph()
    {
        var graph = new StateGraph();
        graph.AddToil(new LordToil_RimlinkIdle());
        return graph;
    }
}

public sealed class LordToil_RimlinkIdle : LordToil
{
    public override bool AllowSatisfyLongNeeds => false;

    public override void UpdateAllDuties()
    {
        foreach (var pawn in lord.ownedPawns) pawn.mindState.duty = new PawnDuty(DutyDefOf.Idle);
    }
}
