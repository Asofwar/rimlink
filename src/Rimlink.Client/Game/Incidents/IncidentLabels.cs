using RimWorld;
using Verse;

namespace Rimlink.Client.Game.Incidents;

/// <summary>A paid incident's name in this game's language: the incident def's label, with the faction for raids (four entries share RaidEnemy).</summary>
public static class IncidentLabels
{
    public static string Of(string incidentDef, string factionDef, string fallback)
    {
        string label = DefDatabase<IncidentDef>.GetNamedSilentFail(incidentDef)?.LabelCap.ToString() ?? fallback;
        return factionDef.Length > 0 && DefDatabase<FactionDef>.GetNamedSilentFail(factionDef) is { } faction
            ? $"{label} ({faction.LabelCap})"
            : label;
    }
}
