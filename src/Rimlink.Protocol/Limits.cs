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

    public const string NameRegex = @"^[\p{L}\p{N} _\-.']{2,32}$";
}
