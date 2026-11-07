// Boundary doubles only: the linked production rule selects real WorldObject types and tile layers.
namespace RimWorld.Planet
{
    public sealed class PlanetLayer { public bool IsRootSurface; }
    public readonly record struct PlanetTile(int tileId, PlanetLayer Layer);
    public class WorldObject { public PlanetTile Tile; }
    public sealed class Caravan : WorldObject { }
    public sealed class Site : WorldObject { }
}
namespace Rimlink.Client.Game.Outposts
{
    public sealed class OutpostWorldObject : RimWorld.Planet.WorldObject
    {
        public long OwnerPlayerId;
        public Rimlink.Protocol.V1.OutpostSummary Summary = new();
    }
}
