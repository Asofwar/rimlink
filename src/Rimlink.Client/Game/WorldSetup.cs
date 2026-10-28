using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Rimlink.Protocol.V1;
using Verse;

namespace Rimlink.Client.Game;

/// <summary>Translates between the server's <see cref="WorldParams"/> and the game's world-generation inputs.</summary>
public static class WorldSetup
{
    /// <summary>Parameters of the world currently loaded in this client (the admin's, right after creating it).</summary>
    public static (WorldParams Params, List<int> NpcTiles) CollectParams(RimWorld.Planet.World world)
    {
        var info = world.info;
        var parameters = new WorldParams
        {
            Seed = info.seedString,
            PlanetCoverage = info.planetCoverage,
            Rainfall = (int)info.overallRainfall,
            Temperature = (int)info.overallTemperature,
            Population = (int)info.overallPopulation,
            Pollution = info.pollution,
            LandmarkDensity = (int)info.landmarkDensity,
            ScenarioDef = Find.Scenario?.name ?? "",
            StorytellerDef = Find.Storyteller?.def?.defName ?? "",
            DifficultyDef = Find.Storyteller?.difficultyDef?.defName ?? "",
        };
        if (info.factions != null) parameters.FactionDefs.AddRange(info.factions.Select(f => f.defName));

        var npcTiles = world.worldObjects.Settlements
            .Where(s => s.Faction != null && !s.Faction.IsPlayer)
            .Select(s => (int)s.Tile)
            .Distinct()
            .ToList();
        return (parameters, npcTiles);
    }

    /// <summary>Overwrites the world-creation page's inputs so every client generates the server's planet.</summary>
    public static void ApplyTo(Page_CreateWorldParams page, WorldParams parameters)
    {
        var t = Traverse.Create(page);
        t.Field("seedString").SetValue(parameters.Seed);
        t.Field("planetCoverage").SetValue(parameters.PlanetCoverage);
        t.Field("rainfall").SetValue((OverallRainfall)parameters.Rainfall);
        t.Field("temperature").SetValue((OverallTemperature)parameters.Temperature);
        t.Field("population").SetValue((OverallPopulation)parameters.Population);
        t.Field("landmarkDensity").SetValue((LandmarkDensity)parameters.LandmarkDensity);
        t.Field("pollution").SetValue(parameters.Pollution);
        if (parameters.FactionDefs.Count > 0)
        {
            var defs = parameters.FactionDefs.Select(n => DefDatabase<FactionDef>.GetNamedSilentFail(n)).Where(d => d != null).ToList();
            if (defs.Count == parameters.FactionDefs.Count) t.Field("factions").SetValue(defs);
            else Log.Warning("[Rimlink] some faction defs of the server world are not loaded; NPC settlements may differ");
        }
    }

    public static bool CurrentWorldMatches(WorldParams parameters) =>
        Find.World?.info?.seedString == parameters.Seed;
}
