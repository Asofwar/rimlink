namespace Rimlink.Server.Storage.Entities;

public sealed class ModpackPackage
{
    public string Id { get; set; } = "";
    public string ManifestJson { get; set; } = "";
    public string? SettingsJson { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UploadExpiresAt { get; set; }
    public string Creator { get; set; } = "";
    public bool RedistributionConfirmed { get; set; }
    public bool PublicDistributionConfirmed { get; set; }
    public bool SharedSettingsConfirmed { get; set; }
    public bool Verified { get; set; }
}

public sealed class ModpackPublication
{
    public int Id { get; set; } = 1;
    public string? PackageId { get; set; }
    public string? PreviousPackageId { get; set; }
}
