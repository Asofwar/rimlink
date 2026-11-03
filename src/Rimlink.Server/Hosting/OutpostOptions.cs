namespace Rimlink.Server.Hosting;

public sealed class OutpostOptions
{
    public const string Section = "Outposts";
    public bool Enabled { get; set; }
    public string AnchorDirectory { get; set; } = "";
    public string StorageContract { get; set; } = "";
    public string QualifiedLinuxFileSystem { get; set; } = "";
}
