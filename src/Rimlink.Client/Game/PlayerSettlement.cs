using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace Rimlink.Client.Game;

/// <summary>Another player's settlement on the world map. Not saved: rebuilt from the server on every login.</summary>
public sealed class PlayerSettlement : WorldObject
{
    public long ObjectId;
    public string OwnerName = "";
    public string SettlementName = "";
    public uint ColorArgb = 0xFF4A90D9;

    public override string Label => SettlementName.Length > 0 ? SettlementName : OwnerName;

    public override Color ExpandingIconColor => new(
        ((ColorArgb >> 16) & 0xFF) / 255f,
        ((ColorArgb >> 8) & 0xFF) / 255f,
        (ColorArgb & 0xFF) / 255f,
        ((ColorArgb >> 24) & 0xFF) / 255f);

    public override string GetInspectString() => "Rimlink.Settlement.Inspect".Translate(OwnerName);

    public override bool SelectableNow => true;
}
