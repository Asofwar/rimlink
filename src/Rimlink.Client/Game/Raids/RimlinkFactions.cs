using RimWorld;
using Verse;

namespace Rimlink.Client.Game.Raids;

/// <summary>Factions the mod needs in the player's world: the hidden raider faction (created on first use) and, for visits, a host faction.</summary>
public static class RimlinkFactions
{
    public const string RaidersDefName = "RimlinkRaiders";

    /// <summary>The permanently hostile faction raiders fight under; generated into this world the first time a raid lands.</summary>
    public static Faction Raiders
    {
        get
        {
            var def = DefDatabase<FactionDef>.GetNamed(RaidersDefName);
            var faction = Find.FactionManager.FirstFactionOfDef(def);
            if (faction is null)
            {
                faction = FactionGenerator.NewGeneratedFaction(new FactionGeneratorParms(def, default, hidden: true));
                Find.FactionManager.Add(faction);
                RimlinkLog.Instance.Info("created the raider faction in this world");
            }
            if (faction.RelationKindWith(Faction.OfPlayer) != FactionRelationKind.Hostile)
                faction.SetRelationDirect(Faction.OfPlayer, FactionRelationKind.Hostile, canSendHostilityLetter: false);
            return faction;
        }
    }
}
