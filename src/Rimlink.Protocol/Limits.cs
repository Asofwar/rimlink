namespace Rimlink.Protocol;

/// <summary>Protocol-wide limits shared by server and client (values from the core design spec).</summary>
public static class Limits
{
    public const int MaxSaveBytes = 64 * 1024 * 1024;
    public const int SaveChunkBytes = 1024 * 1024;
    public const int MaxMessageBytes = 1024 * 1024;

    public const int ChatMaxLength = 500;
    public const int ChatStored = 500;
    public const int ChatOnLogin = 50;

    public const int NameMaxLength = 32;
    public const string NameRegex = @"^[\p{L}\p{N} _\-.']{2,32}$";

    // Economy: the client pre-checks these before taking anything off the map; the server enforces them.
    // Below MaxMessageBytes: a parcel travels inside one gRPC message (Send/Buy/Get replies).
    public const int EconomyMaxParcelBytes = 900 * 1024;
    public const int EconomyMaxItemsPerParcel = 200;
    public const int EconomyMaxPawnsPerParcel = 10;

    // Shared mod settings travel inside the login reply (ModpackDiff.configs): the total stays below MaxMessageBytes.
    public const int MaxModConfigBytes = 256 * 1024;
    public const int MaxModConfigsTotalBytes = 768 * 1024;
    public const int MaxModConfigs = 64;
}
