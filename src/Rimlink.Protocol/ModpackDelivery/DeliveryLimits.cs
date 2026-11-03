namespace Rimlink.Protocol.ModpackDelivery;

public static class DeliveryLimits
{
    public const int FormatVersion = 1, MaxManifestBytes = 1024 * 1024, MaxMods = 512, MaxEntries = 100_000;
    public const long MaxArchiveBytes = 2L * 1024 * 1024 * 1024, MaxExpandedBytes = 8L * 1024 * 1024 * 1024, MaxFileBytes = 512L * 1024 * 1024;
    public const long SpareBytes = 512L * 1024 * 1024;
    public const long MaxServerStorageBytes = 14L * 1024 * 1024 * 1024;
    public const int MaxCompressionRatio = 100, MaxDepth = 16, MaxRelativePath = 180, MaxFullPath = 240, BufferBytes = 64 * 1024;
    public const int MaxGrants = 64, MaxTransfers = 4, AttemptsPerMinute = 5, SimpleBodyBytes = 8 * 1024;
    public static readonly TimeSpan AccessLifetime = TimeSpan.FromMinutes(15);
    public const string GameVersion = "1.7";
}
