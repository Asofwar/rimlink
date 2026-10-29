using RimWorld;
using RimWorld.Planet;
using Verse;

namespace Rimlink.Client.Game.Economy;

/// <summary>
/// A pawn leaves this game for good: sent to the server, gone home after a raid or a visit, a copy whose session is over.
/// Vanilla's discard path (despawn, out of every holder and of the world pawns, then <see cref="WorldPawns.PassToWorld"/> with
/// <see cref="PawnDiscardDecideMode.Discard"/>). A plain <c>Destroy</c> passes the pawn to the world pawns as a dead copy that
/// keeps its ids and is saved forever; a copy that comes back with the same ids (a refused transfer put back) then equals it
/// (<see cref="Thing"/> equality is by id) and is taken for a world pawn.
/// </summary>
public static class PawnExit
{
    public static void Remove(Pawn pawn)
    {
        if (pawn.Discarded) return;
        if (pawn.Spawned) pawn.DeSpawn();
        pawn.holdingOwner?.Remove(pawn);
        var world = Find.WorldPawns;
        if (world.Contains(pawn)) world.RemovePawn(pawn);
        world.PassToWorld(pawn, PawnDiscardDecideMode.Discard);
    }
}
