using RimWorld.Planet;
using Rimlink.Protocol.V1;
using WorldObject = RimWorld.Planet.WorldObject;

namespace Rimlink.Client.Game.Outposts;

internal static class OutpostTileAvailability
{
    public static bool Check(Caravan caravan, PlanetLayer surface, IEnumerable<WorldObject> objects,
        OutpostManifestDocument manifest, OutpostProfile profile, OutpostSummary? target, long ownerPlayerId)
    {
        int tile = caravan.Tile.tileId;
        if (caravan.Tile.Layer != surface || !surface.IsRootSurface || tile < 0 || tile >= manifest.TileCount
            || manifest.Tiles[tile] is not { Land: true, CaravanPassable: true } t || profile == OutpostProfile.Wood && !t.NaturalTrees)
            return false;
        if (target is not null && (ownerPlayerId <= 0 || target.OutpostId.Length == 0 || target.SurfaceId != "planet"
            || target.Tile != tile || target.Lifecycle != "Active" || target.Profile != profile)) return false;
        foreach (var item in objects)
        {
            if (item.Tile.Layer != surface || item.Tile.tileId != tile || ReferenceEquals(item, caravan)) continue;
            if (target is not null && item is OutpostWorldObject marker && marker.OwnerPlayerId == ownerPlayerId
                && marker.Summary.OutpostId == target.OutpostId && marker.Summary.SurfaceId == "planet" && marker.Summary.Tile == tile
                && marker.Summary.Lifecycle == "Active" && marker.Summary.Profile == profile) continue;
            return false;
        }
        return true;
    }
}
