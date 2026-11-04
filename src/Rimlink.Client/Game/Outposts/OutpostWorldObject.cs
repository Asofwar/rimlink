using RimWorld;
using RimWorld.Planet;
using Rimlink.Protocol.V1;
using Verse;

namespace Rimlink.Client.Game.Outposts;

/// <summary>A private unsaved marker, deliberately not a MapParent and never a territorial right.</summary>
public sealed class OutpostWorldObject : RimWorld.Planet.WorldObject
{
    public long OwnerPlayerId;
    public OutpostSummary Summary = new();
    public override string Label => "Rimlink.Outposts.Marker".Translate((Summary.Profile == OutpostProfile.Wood ? "Rimlink.Outposts.Wood" : "Rimlink.Outposts.Steel").Translate());
    public override bool SelectableNow => true;
    public override string GetInspectString() => "Rimlink.Outposts.Summary".Translate(Summary.Stock, Summary.Capacity, Summary.UnitsPerHour.ToString("0.##"))
        + "\n" + "Rimlink.Outposts.NoMap".Translate();
    public override IEnumerable<Gizmo> GetGizmos()
    {
        foreach (var gizmo in base.GetGizmos()) yield return gizmo;
        if (RimlinkSession.MyPlayerId == OwnerPlayerId && PawnCustodyRegistry.IsOwnGame)
            yield return new Command_Action { defaultLabel = "Rimlink.Outposts.Title".Translate(), defaultDesc = "Rimlink.Outposts.NoMap".Translate(),
                action = () => Find.WindowStack.Add(new UI.Outposts.Dialog_Outpost(Summary.OutpostId)) };
    }
}
