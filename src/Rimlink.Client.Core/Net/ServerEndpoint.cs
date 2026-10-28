namespace Rimlink.Client.Net;

public sealed record ServerEndpoint(string Host, int Port, string Name = "")
{
    /// <summary>Stable key for pins and save folders.</summary>
    public string Id => $"{Host}_{Port}".ToLowerInvariant();

    public Uri Address => new($"https://{Host}:{Port}/");

    public string Display => string.IsNullOrWhiteSpace(Name) ? $"{Host}:{Port}" : Name;

    /// <summary>Settings line format "name|host|port".</summary>
    public string ToSettingsLine() => $"{Name}|{Host}|{Port}";

    public static ServerEndpoint? FromSettingsLine(string line)
    {
        string[] parts = line.Split('|');
        if (parts.Length != 3 || !int.TryParse(parts[2], out int port) || string.IsNullOrWhiteSpace(parts[1])) return null;
        return new ServerEndpoint(parts[1].Trim(), port, parts[0].Trim());
    }
}
