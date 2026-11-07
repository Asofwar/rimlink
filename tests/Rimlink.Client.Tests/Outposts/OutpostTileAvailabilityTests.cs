using RimWorld.Planet;
using Rimlink.Client.Game.Outposts;
using Rimlink.Protocol.V1;
using Xunit;
using WorldObject = RimWorld.Planet.WorldObject;

namespace Rimlink.Client.Tests.Outposts;

public sealed class OutpostTileAvailabilityTests
{
    private readonly PlanetLayer surface = new() { IsRootSurface = true };
    private readonly OutpostManifestDocument manifest = new() { TileCount = 1,
        Tiles = { new OutpostManifestTile { Land = true, CaravanPassable = true, NaturalTrees = true } } };
    private OutpostSummary Own() => new() { OutpostId = "own", SurfaceId = "planet", Tile = 0, Lifecycle = "Active", Profile = OutpostProfile.Wood };
    private sealed class ModWorldObject : WorldObject;

    [Theory]
    [InlineData("site")]
    [InlineData("mod-object")]
    [InlineData("other-caravan")]
    [InlineData("found-on-own")]
    [InlineData("foreign-owner")]
    [InlineData("other-outpost")]
    [InlineData("inactive")]
    [InlineData("wrong-metadata-tile")]
    public void Local_occupation_is_checked_before_prepare_and_again_before_removal(string obstacle)
    {
        var caravan = new Caravan { Tile = new(0, surface) }; var target = Own();
        Assert.True(OutpostTileAvailability.Check(caravan, surface, [caravan], manifest, OutpostProfile.Wood, target, 7));
        WorldObject occupant = obstacle switch
        {
            "site" => new Site(), "mod-object" => new ModWorldObject(), "other-caravan" => new Caravan(),
            _ => new OutpostWorldObject { OwnerPlayerId = obstacle == "foreign-owner" ? 8 : 7, Summary = Own() }
        };
        occupant.Tile = caravan.Tile;
        if (occupant is OutpostWorldObject marker)
        {
            if (obstacle == "other-outpost") marker.Summary.OutpostId = "another";
            if (obstacle == "inactive") marker.Summary.Lifecycle = "SafetyHold";
            if (obstacle == "wrong-metadata-tile") marker.Summary.Tile = 1;
        }
        Assert.False(OutpostTileAvailability.Check(caravan, surface, [caravan, occupant], manifest, OutpostProfile.Wood,
            obstacle == "found-on-own" ? null : target, 7));
    }

    [Fact]
    public void Only_the_exact_own_active_assign_marker_is_exempt_and_other_layers_do_not_occupy_the_surface()
    {
        var caravan = new Caravan { Tile = new(0, surface) }; var target = Own();
        var marker = new OutpostWorldObject { Tile = caravan.Tile, OwnerPlayerId = 7, Summary = target.Clone() };
        var underground = new ModWorldObject { Tile = new(0, new PlanetLayer()) };
        Assert.True(OutpostTileAvailability.Check(caravan, surface, [caravan, marker, underground], manifest, OutpostProfile.Wood, target, 7));
        Assert.False(OutpostTileAvailability.Check(caravan, new PlanetLayer(), [caravan], manifest, OutpostProfile.Wood, null, 7));
        target.Lifecycle = "Archived";
        Assert.False(OutpostTileAvailability.Check(caravan, surface, [caravan], manifest, OutpostProfile.Wood, target, 7));
    }
}
