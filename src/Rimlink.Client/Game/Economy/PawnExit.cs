using System.Reflection;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace Rimlink.Client.Game.Economy;

/// <summary>
/// A pawn leaves this game for good: sent to the server, gone home after a raid or a visit, a copy whose session is over.
/// Vanilla's discard path (despawn, out of every holder and of the world pawns, then <c>WorldPawns.DiscardPawn</c>). A plain
/// <c>Destroy</c> passes the pawn to the world pawns as a dead copy that keeps its ids and is saved forever; a copy that comes
/// back with the same ids (a refused transfer put back) then equals it (<see cref="Thing"/> equality is by id) and is taken for
/// a world pawn.
/// </summary>
public static class PawnExit
{
    /// <summary>The world pawn GC's quiet discard: the play log and tale entries about the pawn go without a warning each.</summary>
    private static readonly MethodInfo? DiscardPawn = AccessTools.Method(typeof(WorldPawns), "DiscardPawn", [typeof(Pawn), typeof(bool)]);

    public static void Remove(Pawn pawn)
    {
        if (pawn.Discarded) return;
        if (pawn.Spawned) pawn.DeSpawn();
        pawn.holdingOwner?.Remove(pawn);
        var world = Find.WorldPawns;
        if (world.Contains(pawn)) world.RemovePawn(pawn);
        if (DiscardPawn is null)
        {
            world.PassToWorld(pawn, PawnDiscardDecideMode.Discard);
            return;
        }
        if (PawnComponentsUtility.HasSpawnedComponents(pawn)) PawnComponentsUtility.RemoveComponentsOnDespawned(pawn);
        DiscardPawn.Invoke(world, [pawn, true]);
    }

    /// <summary>
    /// A pawn put back into a caravan after a refusal: caravan members are world pawns (a caravan drops a member that is not one
    /// on its next tick). Added to the caravan first, so the world pawns see a caravan member and not a free pawn to hand away.
    /// </summary>
    public static void BackToCaravan(Caravan caravan, Pawn pawn)
    {
        caravan.AddPawn(pawn, addCarriedPawnToWorldPawnsIfAny: true);
        if (!pawn.IsWorldPawn()) Find.WorldPawns.PassToWorld(pawn);
    }
}
