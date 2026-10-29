using System.Text;
using System.Text.Json;
using Rimlink.Protocol.V1;

namespace Rimlink.Client.Mods;

public readonly record struct SettingsKey(string PackageId, string Handle);

/// <summary>The server whose mod settings the game uses from its start, and for which mod classes.</summary>
public sealed record ServerSettingsSet(string Server, string Display, IReadOnlyList<SettingsKey> Mods);

/// <summary>
/// Shared mod settings downloaded from servers: one folder per server with the files byte for byte as the server sent them,
/// and <c>active.json</c> naming the set the game redirects to at its next start. The player's own Config files are not here.
/// </summary>
public sealed class ServerSettingsStore(string root)
{
    private string ActivePath => Path.Combine(root, "active.json");

    /// <summary>A folder name for a server: lower-case host and port, anything but letters, digits, '.' and '-' as '_'.</summary>
    public static string ServerKey(string host, int port)
    {
        var key = new StringBuilder();
        foreach (char c in host.ToLowerInvariant()) key.Append(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' ? c : '_');
        return $"{key}_{port}";
    }

    public string PathFor(string server, SettingsKey key) => Path.Combine(root, server, $"Mod_{key.PackageId}_{key.Handle}.xml");

    /// <summary>Writes the server's files and makes this server's set the active one.</summary>
    public void Activate(string server, string display, IEnumerable<ModConfig> configs)
    {
        var keys = new List<SettingsKey>();
        foreach (var config in configs)
        {
            var key = new SettingsKey(config.PackageId, config.Handle);
            string path = PathFor(server, key);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, config.Content.ToByteArray());
            keys.Add(key);
        }
        Directory.CreateDirectory(root);
        File.WriteAllText(ActivePath, JsonSerializer.Serialize(new ServerSettingsSet(server, display, keys)));
    }

    /// <summary>The active set, or null when there is none or the file cannot be read (the game then starts with the player's own).</summary>
    public ServerSettingsSet? ReadActive()
    {
        try
        {
            if (!File.Exists(ActivePath)) return null;
            var set = JsonSerializer.Deserialize<ServerSettingsSet>(File.ReadAllText(ActivePath));
            return set is { Server.Length: > 0, Mods: not null } ? set : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Back to the player's own settings from the next start; the downloaded files stay for a later return.</summary>
    public void Clear()
    {
        if (File.Exists(ActivePath)) File.Delete(ActivePath);
    }
}
