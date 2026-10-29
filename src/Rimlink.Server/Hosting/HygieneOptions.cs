using System.Text.Json.Serialization;

namespace Rimlink.Server.Hosting;

/// <summary>World hygiene: archiving abandoned colonies and the wealth tiers shown on the world map.</summary>
public sealed class HygieneOptions
{
    public const string Section = "Hygiene";

    public static readonly long[] DefaultWealthTiers = [50000, 150000, 400000, 1000000];

    /// <summary>Days without a login before a colony may be archived; 0 turns the sweep off.</summary>
    public int ArchiveAfterDays { get; set; } = 14;
    /// <summary>Only colonies poorer than this are archived; 0 ignores wealth.</summary>
    public long ArchiveMaxWealth { get; set; } = 100000;
    /// <summary>Only colonies younger than this many game days are archived; 0 ignores age.</summary>
    public int ArchiveMaxColonyDays { get; set; } = 60;
    /// <summary>Hours between sweeps (1–168).</summary>
    public int SweepHours { get; set; } = 6;

    /// <summary>
    /// The four bounds of wealth tiers 1–5 as configured. The binder appends configured array items to a non-empty default,
    /// so the default lives in <see cref="Tiers"/> and this stays null unless the config sets it.
    /// </summary>
    [JsonIgnore]
    public long[]? WealthTiers { get; set; }

    [JsonPropertyName("wealthTiers")]
    public long[] Tiers => WealthTiers ?? DefaultWealthTiers;

    public bool IsValid =>
        ArchiveAfterDays >= 0 && ArchiveMaxWealth >= 0 && ArchiveMaxColonyDays >= 0 && SweepHours is >= 1 and <= 168
        && Tiers.Length == 4 && Tiers[0] > 0 && Tiers.Zip(Tiers.Skip(1)).All(p => p.First < p.Second);
}
