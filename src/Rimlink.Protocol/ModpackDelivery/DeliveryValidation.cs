using System.Text;
using Rimlink.Protocol.V1;

namespace Rimlink.Protocol.ModpackDelivery;

public static class DeliveryValidation
{
    public static bool Sha256(string? value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    public static bool Id(string? value) => value is { Length: 32 } && Guid.TryParseExact(value, "N", out _) && value == value.ToLowerInvariant();
    public static bool Official(string id) => id.StartsWith("ludeon.", StringComparison.Ordinal);
    public static string Normalize(string? id)
    {
        string clean = ModpackComparison.Normalize(id ?? "");
        if (clean.Length is 0 or > 200 || clean.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '.' and not '_' and not '-')) throw new DeliveryException("InvalidManifest");
        return clean;
    }

    public static CollectionDescriptor Describe(string gameVersion, IEnumerable<DeliveryEntry> entries, IEnumerable<SharedSettingHash> settings)
    {
        if (gameVersion != DeliveryLimits.GameVersion) throw new DeliveryException("GameVersion");
        if (entries is null || settings is null) throw new DeliveryException("InvalidManifest");
        var list = entries.Select(e => e is null ? throw new DeliveryException("InvalidManifest") : e with { PackageId = Normalize(e.PackageId), Version = e.Version?.Trim() ?? "", FilesHash = e.FilesHash?.ToLowerInvariant() ?? "" }).OrderBy(e => e.Position).ToArray();
        if (list.Length is 0 or > DeliveryLimits.MaxMods || list.Select(e => e.PackageId).Distinct().Count() != list.Length || list.Select(e => e.Position).Distinct().Count() != list.Length
            || list.Any(e => e.Position < 0 || e.Version.Length > 64 || e.Version.Any(char.IsControl) || e.Kind is not EntryKind.Required and not EntryKind.Allowed || e.FilesHash.Length > 0 && !Sha256(e.FilesHash))) throw new DeliveryException("InvalidManifest");
        var configList = settings.Select(s => s is null ? throw new DeliveryException("InvalidManifest") : s with { PackageId = Normalize(s.PackageId) }).OrderBy(s => s.PackageId, StringComparer.Ordinal).ThenBy(s => s.Handle, StringComparer.Ordinal).ToArray();
        if (configList.Length > Limits.MaxModConfigs || configList.DistinctBy(s => (s.PackageId, s.Handle)).Count() != configList.Length
            || configList.Any(s => !SafeHandle(s.Handle) || !Sha256(s.Hash) && s.Hash != ModContent.NoSettings)) throw new DeliveryException("InvalidManifest");
        string revision = ModContent.Hash(DeliveryJson.Write(new { gameVersion, entries = list }));
        return new CollectionDescriptor(revision, gameVersion, list, ModContent.Hash(DeliveryJson.Write(new { revision, configs = configList })));
    }

    public static bool SafeHandle(string? value) => value is { Length: > 0 and <= 128 } && value.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');
    public static void Validate(PackageManifest p, bool requirePackageId = false)
    {
        if (p is null || p.FormatVersion != DeliveryLimits.FormatVersion || p.PackageId is null || (requirePackageId || p.PackageId.Length > 0) && !Id(p.PackageId)
            || !Sha256(p.CollectionRevision) || !Sha256(p.CatalogHash) || !Sha256(p.ArchiveSha256)
            || p.ArchiveBytes is <= 0 or > DeliveryLimits.MaxArchiveBytes || p.ExpandedBytes is < 0 or > DeliveryLimits.MaxExpandedBytes
            || p.FileCount is <= 0 or > DeliveryLimits.MaxEntries || p.Mods is null || p.Entries is null || p.Prerequisites is null || p.Mods.Any(m => m is null)) throw new DeliveryException("InvalidManifest");
        var descriptor = Describe(p.GameVersion, p.Entries, []);
        if (descriptor.Revision != p.CollectionRevision || !descriptor.Entries.SequenceEqual(p.Entries) || p.Mods.Length is 0 or > DeliveryLimits.MaxMods || p.Mods.Select(m => m.PackageId).Distinct().Count() != p.Mods.Length
            || p.Mods.Select(m => m.FolderId).Distinct().Count() != p.Mods.Length) throw new DeliveryException("InvalidManifest");
        var required = descriptor.Entries.Where(e => e.Kind == EntryKind.Required).ToDictionary(e => e.PackageId);
        if (!required.ContainsKey("ludeon.rimworld")) throw new DeliveryException("Prerequisite");
        var official = required.Values.Where(e => Official(e.PackageId)).ToArray();
        if (!p.Prerequisites.OrderBy(e => e.Position).SequenceEqual(official)) throw new DeliveryException("InvalidManifest");
        var expected = required.Values.Where(e => !Official(e.PackageId)).ToDictionary(e => e.PackageId);
        if (expected.Count != p.Mods.Length) throw new DeliveryException("Incomplete");
        long bytes = 0; int files = 0;
        foreach (var mod in p.Mods)
        {
            if (mod is null || !expected.TryGetValue(mod.PackageId, out var entry) || mod.PackageId != Normalize(mod.PackageId) || !Id(mod.FolderId) || !Sha256(mod.FilesHash) || !Sha256(mod.ContentHash)
                || mod.Position != entry.Position || mod.Version is null || mod.Version.Length > 64 || entry.Version.Length > 0 && mod.Version != entry.Version || entry.FilesHash.Length > 0 && mod.FilesHash != entry.FilesHash
                || mod.DisplayName is null || Encoding.UTF8.GetByteCount(mod.DisplayName) > 512 || mod.DisplayName.Any(char.IsControl)
                || mod.ExpandedBytes is < 0 or > DeliveryLimits.MaxExpandedBytes || mod.FileCount is <= 0 or > DeliveryLimits.MaxEntries
                || mod.SupportedVersions is null || mod.Dependencies is null || mod.Incompatibilities is null || mod.LoadBefore is null || mod.LoadAfter is null
                || mod.SupportedVersions.Length is 0 or > 32 || mod.SupportedVersions.Any(v => v is null || v.Length > 32) || !mod.SupportedVersions.Contains(p.GameVersion, StringComparer.Ordinal)) throw new DeliveryException("InvalidManifest");
            foreach (var values in new[] { mod.Dependencies, mod.Incompatibilities, mod.LoadBefore, mod.LoadAfter })
                if (values.Length > DeliveryLimits.MaxMods || values.Distinct().Count() != values.Length || values.Any(id => id != Normalize(id))) throw new DeliveryException("InvalidManifest");
            bytes += mod.ExpandedBytes; files += mod.FileCount;
        }
        if (bytes != p.ExpandedBytes || files != p.FileCount || bytes > DeliveryLimits.MaxExpandedBytes || files > DeliveryLimits.MaxEntries) throw new DeliveryException("InvalidManifest");
        ValidateRelations(p.Mods, required);
        if (DeliveryJson.Write(p).Length > DeliveryLimits.MaxManifestBytes) throw new DeliveryException("Limit");
    }

    public static void ValidateRelations(IEnumerable<DeliveryMod> mods, IReadOnlyDictionary<string, DeliveryEntry> required)
    {
        foreach (var mod in mods)
        {
            foreach (string dependency in mod.Dependencies)
                if (!required.TryGetValue(dependency, out var entry) || entry.Position >= mod.Position) throw new DeliveryException("Dependency");
            if (mod.Incompatibilities.Any(required.ContainsKey)) throw new DeliveryException("Incompatible");
            foreach (string before in mod.LoadBefore)
                if (required.TryGetValue(before, out var entry) && entry.Position <= mod.Position) throw new DeliveryException("Order");
            foreach (string after in mod.LoadAfter)
                if (required.TryGetValue(after, out var entry) && entry.Position >= mod.Position) throw new DeliveryException("Order");
        }
    }

    public static void Validate(SettingsSnapshot settings, PackageManifest manifest)
    {
        if (settings.PackageId != manifest.PackageId || settings.CatalogHash != manifest.CatalogHash || settings.Configs is null || settings.Configs.Length > Limits.MaxModConfigs) throw new DeliveryException("InvalidManifest");
        long bytes = 0;
        foreach (var config in settings.Configs)
        {
            if (config is null || !SafeHandle(config.Handle) || config.PackageId != Normalize(config.PackageId) || config.PackageId.Length + config.Handle.Length > DeliveryLimits.MaxRelativePath - 10 || !manifest.Entries.Any(e => e.PackageId == config.PackageId) || config.Content is null || config.Content.Length > Limits.MaxModConfigBytes
                || ModContent.SettingsHash(config.Content) != config.Hash) throw new DeliveryException("InvalidManifest");
            bytes += config.Content.Length;
        }
        if (bytes > Limits.MaxModConfigsTotalBytes || Describe(manifest.GameVersion, manifest.Entries, settings.Configs.Select(c => new SharedSettingHash(c.PackageId, c.Handle, c.Hash))).CatalogHash != manifest.CatalogHash) throw new DeliveryException("InvalidManifest");
    }
}
