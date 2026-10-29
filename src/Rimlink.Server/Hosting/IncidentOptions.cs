namespace Rimlink.Server.Hosting;

/// <summary>Paid incidents (paid incidents spec §2, §4).</summary>
public sealed class IncidentOptions
{
    public const string Section = "Incidents";

    public bool Enabled { get; set; } = true;
    public int MaxQueuedPerTarget { get; set; } = 2;
    public double BasePrice { get; set; } = 100;
    public long MinPrice { get; set; } = 50;
    public long WarnTicks { get; set; } = 30000;
    public int WarnFromLevel { get; set; } = 3;
    public double KindCooldownHours { get; set; } = 1;
    public double ExpireRealDays { get; set; } = 7;
    /// <summary>What can be ordered; empty means <see cref="DefaultCatalog"/> (a configured list replaces it whole).</summary>
    public List<IncidentCatalogEntry> Catalog { get; set; } = [];

    public IReadOnlyList<IncidentCatalogEntry> EffectiveCatalog => Catalog.Count > 0 ? Catalog : DefaultCatalog;

    public static readonly IReadOnlyList<IncidentCatalogEntry> DefaultCatalog =
    [
        new("pirates", "RaidEnemy", "Pirate", "Hostile", 1, 1.4),
        new("outlanders", "RaidEnemy", "OutlanderRough", "Hostile", 1, 1.4),
        new("tribals", "RaidEnemy", "TribeRough", "Hostile", 1, null),
        new("mechanoids", "RaidEnemy", "Mechanoid", "Hostile", 5, 1.5),
        new("infestation", "Infestation", null, "Hostile", 3, null),
        new("toxic-fallout", "ToxicFallout", null, "Weather", 2, null),
        new("cold-snap", "ColdSnap", null, "Weather", 1, null),
        new("heat-wave", "HeatWave", null, "Weather", 1, null),
        new("flashstorm", "Flashstorm", null, "Weather", 1, null),
        new("solar-flare", "SolarFlare", null, "Weather", 1, null),
        new("trader", "TraderCaravanArrival", null, "Kind", 1, null),
        new("resource-pod", "ResourcePodCrash", null, "Kind", 1, null),
        new("wanderer", "WandererJoin", null, "Kind", 2, null),
    ];
}

/// <summary>One orderable event. <see cref="Kind"/> is "Hostile", "Weather" or "Kind"; drop pods are offered when <see cref="PodsMultiplier"/> is set.</summary>
public sealed class IncidentCatalogEntry
{
    public IncidentCatalogEntry() { }

    public IncidentCatalogEntry(string id, string incidentDef, string? factionDef, string kind, double priceMultiplier, double? podsMultiplier)
    {
        Id = id;
        IncidentDef = incidentDef;
        FactionDef = factionDef;
        Kind = kind;
        PriceMultiplier = priceMultiplier;
        PodsMultiplier = podsMultiplier;
    }

    public string Id { get; set; } = "";
    public string IncidentDef { get; set; } = "";
    public string? FactionDef { get; set; }
    public string Kind { get; set; } = "Hostile";
    public double PriceMultiplier { get; set; } = 1;
    public double? PodsMultiplier { get; set; }
}
