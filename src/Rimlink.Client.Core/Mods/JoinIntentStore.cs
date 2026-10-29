using System.Text.Json;

namespace Rimlink.Client.Mods;

/// <summary>The server to log in to after "Fix and restart"; <see cref="Login"/> only for a password account (no password kept).</summary>
public sealed record JoinIntent(string Host, int Port, string Display, string? Login, DateTime CreatedUtc);

/// <summary>
/// One join intent across a game restart. <see cref="Take"/> always deletes the file, so a login that fails again after the
/// restart shows its window instead of restarting in a loop.
/// </summary>
public sealed class JoinIntentStore(string path)
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(15);

    public void Save(JoinIntent intent)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(intent));
    }

    /// <summary>The intent if it is at most <see cref="MaxAge"/> old; null when there is none, it is stale or unreadable.</summary>
    public JoinIntent? Take(DateTime nowUtc)
    {
        if (!File.Exists(path)) return null;
        JoinIntent? intent = null;
        try { intent = JsonSerializer.Deserialize<JoinIntent>(File.ReadAllText(path)); }
        catch (Exception ex) when (ex is JsonException or IOException or NotSupportedException) { }
        finally { try { File.Delete(path); } catch (IOException) { } }
        return intent is not null && nowUtc - intent.CreatedUtc <= MaxAge ? intent : null;
    }
}
