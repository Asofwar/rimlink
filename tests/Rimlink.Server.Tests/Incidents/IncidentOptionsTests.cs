using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Rimlink.Server.Hosting;
using Rimlink.Server.Tests.Support;
using Xunit;

namespace Rimlink.Server.Tests.Incidents;

public class IncidentOptionsTests
{
    [Fact]
    public void DefaultsMatchTheSpec()
    {
        using var fixture = new ServerFixture();

        var o = fixture.Services.GetRequiredService<IOptions<IncidentOptions>>().Value;

        Assert.True(o.Enabled);
        Assert.Equal(2, o.MaxQueuedPerTarget);
        Assert.Equal(100, o.BasePrice);
        Assert.Equal(50, o.MinPrice);
        Assert.Equal(30000, o.WarnTicks);
        Assert.Equal(3, o.WarnFromLevel);
        Assert.Equal(1, o.KindCooldownHours);
        Assert.Equal(7, o.ExpireRealDays);
        var catalog = o.EffectiveCatalog;
        Assert.Equal(13, catalog.Count);
        var pirates = catalog.Single(e => e.Id == "pirates");
        Assert.Equal(("RaidEnemy", "Pirate", "Hostile", 1.0, 1.4), (pirates.IncidentDef, pirates.FactionDef, pirates.Kind, pirates.PriceMultiplier, pirates.PodsMultiplier));
        var mechanoids = catalog.Single(e => e.Id == "mechanoids");
        Assert.Equal((5.0, 1.5), (mechanoids.PriceMultiplier, mechanoids.PodsMultiplier));
        var infestation = catalog.Single(e => e.Id == "infestation");
        Assert.Equal((3.0, (double?)null), (infestation.PriceMultiplier, infestation.PodsMultiplier));
        Assert.Equal(("Weather", 2.0), (catalog.Single(e => e.Id == "toxic-fallout").Kind, catalog.Single(e => e.Id == "toxic-fallout").PriceMultiplier));
        Assert.Equal(("Kind", 2.0), (catalog.Single(e => e.Id == "wanderer").Kind, catalog.Single(e => e.Id == "wanderer").PriceMultiplier));
    }

    [Fact]
    public void AConfiguredCatalogReplacesTheDefaults()
    {
        using var fixture = new ServerFixture()
            .WithSetting("Incidents:Catalog:0:Id", "only")
            .WithSetting("Incidents:Catalog:0:IncidentDef", "RaidEnemy")
            .WithSetting("Incidents:Catalog:0:Kind", "Hostile")
            .WithSetting("Incidents:Catalog:0:PriceMultiplier", "2");

        var catalog = fixture.Services.GetRequiredService<IOptions<IncidentOptions>>().Value.EffectiveCatalog;

        Assert.Equal("only", Assert.Single(catalog).Id);
    }
}
