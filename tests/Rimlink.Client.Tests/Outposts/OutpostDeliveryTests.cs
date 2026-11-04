using Rimlink.Client.Outposts;
using Rimlink.Protocol.V1;
using Xunit;
namespace Rimlink.Client.Tests.Outposts;

public sealed class OutpostDeliveryTests
{
    private static ResourceCatalogSnapshot Catalog() => new("11111111-1111-1111-1111-111111111111", new byte[32], new byte[32],
        [new("WoodLog", 75), new("Steel", 75)], [new("WoodLog", 120), new("Steel", 190)]);
    private static ResourceParcel Wood(int count = 151) => new() { SchemaVersion = 1, DefName = "WoodLog", Count = count, ImportValueX100 = 120L * count };
    [Fact]
    public void Stack_split_preserves_exact_quantity()
    {
        var result = ResourceDeliveryChecks.Validate(Wood(), Catalog());
        Assert.True(result.Allowed); Assert.Null(result.RefusalKey);
        Assert.Equal(new[] { 75, 75, 1 }, result.Stacks.Select(s => s.Count));
        Assert.All(result.Stacks, s => Assert.Equal("WoodLog", s.DefName));
    }
    [Fact]
    public void A_full_warehouse_is_not_truncated_to_one_stack()
    {
        var result = ResourceDeliveryChecks.Validate(Wood(1000), Catalog()); Assert.True(result.Allowed);
        Assert.Equal(14, result.Stacks.Count); Assert.Equal(25, result.Stacks[^1].Count);
        Assert.Equal(1000, result.Stacks.Sum(s => s.Count));
    }
    [Theory]
    [InlineData("Silver")][InlineData("missing")][InlineData("quality")][InlineData("stuff")]
    [InlineData("zero-stack")][InlineData("negative")][InlineData("over-cap")][InlineData("version")]
    [InlineData("valuation")][InlineData("duplicate-def")][InlineData("duplicate-value")][InlineData("digest")]
    public void Unsupported_resource_does_not_offer_a_partial_spawn_plan(string fault)
    {
        var resource = Wood(); var catalog = Catalog();
        if (fault == "Silver") resource.DefName = "Silver";
        if (fault == "missing") catalog = catalog with { Definitions = [new("Steel", 75)] };
        if (fault == "quality") catalog = catalog with { Definitions = [new("WoodLog", 75, HasQuality: true)] };
        if (fault == "stuff") catalog = catalog with { Definitions = [new("WoodLog", 75, SimpleUnstuffed: false)] };
        if (fault == "zero-stack") catalog = catalog with { Definitions = [new("WoodLog", 0)] };
        if (fault == "negative") resource.Count = -1;
        if (fault == "over-cap") resource = Wood(1001);
        if (fault == "version") resource.SchemaVersion = 2;
        if (fault == "valuation") resource.ImportValueX100++;
        if (fault == "duplicate-def") catalog = catalog with { Definitions = [new("WoodLog", 75), new("WoodLog", 75)] };
        if (fault == "duplicate-value") catalog = catalog with { Valuations = [new("WoodLog", 120), new("WoodLog", 120)] };
        if (fault == "digest") catalog = catalog with { ManifestDigest = [] };
        var result = ResourceDeliveryChecks.Validate(resource, catalog);
        Assert.False(result.Allowed); Assert.NotNull(result.RefusalKey); Assert.Empty(result.Stacks);
    }
    [Fact]
    public void Steel_uses_its_pinned_valuation_and_actual_stack_limit()
    {
        var result = ResourceDeliveryChecks.Validate(new() { SchemaVersion = 1, DefName = "Steel", Count = 76, ImportValueX100 = 14440 }, Catalog());
        Assert.True(result.Allowed); Assert.Equal(new[] { new ResourceStack("Steel", 75), new ResourceStack("Steel", 1) }, result.Stacks);
    }
}
