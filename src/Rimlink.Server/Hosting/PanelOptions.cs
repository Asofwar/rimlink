namespace Rimlink.Server.Hosting;

/// <summary>Web admin panel and public page (admin panel spec §2).</summary>
public sealed class PanelOptions
{
    public const string Section = "Panel";

    public bool Enabled { get; set; } = true;
    public bool Public { get; set; } = true;
    public bool PublicPlayerNames { get; set; } = true;
    public int SessionHours { get; set; } = 12;
    public string ServerName { get; set; } = "";
    public string Description { get; set; } = "";
    /// <summary>Address shown on the public page for connecting; empty = the host and port of the request.</summary>
    public string PublicAddress { get; set; } = "";

    public string DisplayName => string.IsNullOrWhiteSpace(ServerName) ? "Rimlink" : ServerName.Trim();

    public string DescriptionText
    {
        get
        {
            string text = Description.Trim();
            return text.Length <= 1000 ? text : text[..1000];
        }
    }

    public TimeSpan SessionLength => TimeSpan.FromHours(Math.Clamp(SessionHours, 1, 168));
}
