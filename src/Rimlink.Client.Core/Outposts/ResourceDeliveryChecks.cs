using Rimlink.Protocol.V1;
namespace Rimlink.Client.Outposts;

public sealed record ResourceDefinitionSnapshot(string DefName, int StackLimit, bool SimpleUnstuffed = true, bool HasQuality = false);
public sealed record ResourceValuationSnapshot(string DefName, long UnitValueX100);
public sealed record ResourceCatalogSnapshot(string ManifestId, byte[] ManifestDigest, byte[] GameDefsSha256,
    IReadOnlyList<ResourceDefinitionSnapshot> Definitions, IReadOnlyList<ResourceValuationSnapshot> Valuations);
public sealed record ResourceStack(string DefName, int Count);
public sealed record DeliveryDecision(bool Allowed, string? RefusalKey, IReadOnlyList<ResourceStack> Stacks);

/// <summary>Pure complete validation before any ThingMaker call; no fallback item or partial plan.</summary>
public static class ResourceDeliveryChecks
{
    public static DeliveryDecision Validate(ResourceParcel payload, ResourceCatalogSnapshot catalog)
    {
        DeliveryDecision Refuse() => new(false, "Rimlink.Outposts.DeliveryIncompatible", []);
        if (payload is null || catalog is null || payload.SchemaVersion != 1 || payload.DefName is not ("WoodLog" or "Steel")
            || payload.Count is < 1 or > 1000 || catalog.ManifestDigest.Length != 32 || catalog.GameDefsSha256.Length != 32
            || !Guid.TryParse(catalog.ManifestId, out var manifest) || manifest == Guid.Empty
            || catalog.Definitions.Count > 10000 || catalog.Valuations.Count > 2
            || catalog.Definitions.Select(d => d.DefName).Distinct(StringComparer.Ordinal).Count() != catalog.Definitions.Count
            || catalog.Valuations.Select(d => d.DefName).Distinct(StringComparer.Ordinal).Count() != catalog.Valuations.Count) return Refuse();
        var definition = catalog.Definitions.SingleOrDefault(d => d.DefName == payload.DefName);
        var value = catalog.Valuations.SingleOrDefault(d => d.DefName == payload.DefName);
        if (definition is null || definition.StackLimit <= 0 || !definition.SimpleUnstuffed || definition.HasQuality
            || value is null || value.UnitValueX100 is < 0 or > long.MaxValue / 1000
            || payload.ImportValueX100 != value.UnitValueX100 * payload.Count) return Refuse();
        var stacks = new List<ResourceStack>(); int remaining = payload.Count;
        while (remaining > 0) { int count = Math.Min(remaining, definition.StackLimit); stacks.Add(new(payload.DefName, count)); remaining -= count; }
        return new(true, null, stacks);
    }
}
