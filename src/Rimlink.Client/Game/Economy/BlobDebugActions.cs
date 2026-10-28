using LudeonTK;
using RimWorld;
using Verse;

namespace Rimlink.Client.Game.Economy;

/// <summary>Dev-mode check of the blob round trip: the selected thing or pawn is serialised, removed and respawned from its blob.</summary>
public static class BlobDebugActions
{
    [DebugAction("Rimlink", "Blob round-trip selected thing", allowedGameStates = AllowedGameStates.PlayingOnMap)]
    public static void BlobRoundTripSelected()
    {
        foreach (var selected in Find.Selector.SelectedObjects.OfType<Thing>().ToList())
        {
            var map = selected.Map;
            var cell = selected.Position;
            try
            {
                if (selected is Pawn pawn)
                {
                    var blob = ThingBlobs.SerializePawn(pawn);
                    string label = pawn.LabelCap;
                    pawn.DeSpawn();
                    var again = ThingBlobs.DeserializePawn(blob);
                    GenSpawn.Spawn(again, cell, map);
                    pawn.Destroy();
                    Log.Message($"[Rimlink] blob round-trip ok: {label} → {again.LabelCap} {blob.ScribeXmlZstd.Length} B, skills {blob.Skills.Count}, hediffs {again.health.hediffSet.hediffs.Count}, apparel {again.apparel?.WornApparelCount ?? 0}");
                }
                else
                {
                    var blob = ThingBlobs.SerializeItem(selected);
                    string label = selected.LabelCap;
                    selected.DeSpawn();
                    var again = ThingBlobs.DeserializeItem(blob);
                    GenPlace.TryPlaceThing(again, cell, map, ThingPlaceMode.Direct);
                    selected.Destroy();
                    Log.Message($"[Rimlink] blob round-trip ok: {label} → {again.LabelCap} {blob.ScribeXmlZstd.Length} B, count {blob.Count}→{again.stackCount}, hp {blob.HitPoints}→{(again.def.useHitPoints ? again.HitPoints : 0)}, quality {blob.Quality}");
                }
            }
            catch (Exception ex)
            {
                Log.Error($"[Rimlink] blob round-trip FAILED for {selected}: {ex}");
            }
        }
    }
}
