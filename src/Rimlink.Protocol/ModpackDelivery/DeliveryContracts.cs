using System.Text.Json;
using System.Text.Json.Serialization;
using Rimlink.Protocol.V1;

namespace Rimlink.Protocol.ModpackDelivery;

public sealed record DeliveryEntry(int Position, string PackageId, string Version, string FilesHash, EntryKind Kind)
{
    public ModpackEntry ToProto() => new() { Position = Position, PackageId = PackageId, Version = Version, FilesHash = FilesHash, Kind = Kind };
    public static DeliveryEntry From(ModpackEntry entry) => new(entry.Position, entry.PackageId, entry.Version, entry.FilesHash, entry.Kind);
}
public sealed record SharedSettingHash(string PackageId, string Handle, string Hash);
public sealed record CollectionDescriptor(string Revision, string GameVersion, DeliveryEntry[] Entries, string CatalogHash);
public sealed record SharedConfig(string PackageId, string Handle, string Hash, byte[] Content);
public sealed record SettingsSnapshot(string PackageId, string CatalogHash, SharedConfig[] Configs);

public sealed record DeliveryMod
{
    public string PackageId { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string Version { get; init; } = "";
    public string FilesHash { get; init; } = "";
    public string ContentHash { get; init; } = "";
    public string FolderId { get; init; } = "";
    public int Position { get; init; }
    public string[] SupportedVersions { get; init; } = [];
    public string[] Dependencies { get; init; } = [];
    public string[] Incompatibilities { get; init; } = [];
    public string[] LoadBefore { get; init; } = [];
    public string[] LoadAfter { get; init; } = [];
    public long ExpandedBytes { get; init; }
    public int FileCount { get; init; }
}

public sealed record PackageManifest
{
    public int FormatVersion { get; init; } = DeliveryLimits.FormatVersion;
    public string PackageId { get; init; } = "";
    public string CollectionRevision { get; init; } = "";
    public string CatalogHash { get; init; } = "";
    public string GameVersion { get; init; } = "";
    public string ArchiveSha256 { get; init; } = "";
    public long ArchiveBytes { get; init; }
    public long ExpandedBytes { get; init; }
    public int FileCount { get; init; }
    public DeliveryEntry[] Entries { get; init; } = [];
    public DeliveryEntry[] Prerequisites { get; init; } = [];
    public DeliveryMod[] Mods { get; init; } = [];
}

public sealed record UploadOffer(PackageManifest Manifest, bool RedistributionConfirmed, bool PublicDistributionConfirmed);
public sealed record UploadAccepted(string PackageId, DateTime UploadExpiresAt);
public sealed record PublishPackage(string ExpectedCollectionRevision, string ExpectedCatalogHash, bool SharedSettingsConfirmed);
public sealed record DeliveryPublication(bool Available, string? PackageId = null, string? CollectionRevision = null, string? CatalogHash = null);
public sealed record DeliveryCapabilities(int FormatVersion, bool DeliveryAvailable, bool RequiresServerPassword, bool PasswordLoginAvailable, bool SteamLoginAvailable);
public sealed record AccessRequest(string? ServerPassword, string? ExpectedPackageId = null);
public sealed record AccessGrant(string Token, DateTime ExpiresAt, string PackageId, string CollectionRevision, string CatalogHash);
public sealed record DeliveryError(string Code, string MessageKey, string[] Args, bool Retryable);

public sealed class DeliveryException(string code, string? message = null, bool retryable = false) : IOException(message ?? code)
{
    public string Code { get; } = code;
    public bool Retryable { get; } = retryable;
    public DeliveryError Error => new(Code, "Rimlink.Delivery.Error." + Code, [], Retryable);
}

public static class DeliveryJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();
    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { MaxDepth = 32, PropertyNameCaseInsensitive = false, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, AllowDuplicateProperties = false };
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
    public static byte[] Write<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, Options);
    public static T Read<T>(ReadOnlySpan<byte> bytes, int limit = DeliveryLimits.MaxManifestBytes)
    {
        if (bytes.Length > limit) throw new DeliveryException("Limit");
        try { return JsonSerializer.Deserialize<T>(bytes, Options) ?? throw new DeliveryException("InvalidManifest"); }
        catch (JsonException) { throw new DeliveryException("InvalidManifest"); }
    }
}
