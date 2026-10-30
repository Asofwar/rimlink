namespace Rimlink.Server.Hosting;

/// <summary>How saves are kept on disk.</summary>
public sealed class SaveStorageOptions
{
    public const string Section = "Saves";

    /// <summary>Turns old versions into deltas; false stops making new ones, existing deltas stay readable.</summary>
    public bool CompactOldVersions { get; set; } = true;
}
