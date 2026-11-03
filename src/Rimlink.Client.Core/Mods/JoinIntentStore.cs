using System.Text.Json;
using Rimlink.Client.Mods.Delivery;
using Rimlink.Protocol.ModpackDelivery;

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
        InstallFiles.Write(path, JsonSerializer.SerializeToUtf8Bytes(intent));
    }

    /// <summary>The intent if it is at most <see cref="MaxAge"/> old; null when there is none, it is stale or unreadable.</summary>
    public JoinIntent? Take(DateTime nowUtc)
    {
        if (!File.Exists(path)) return null;
        JoinIntent? intent = null;
        bool consumed = false;
        try { intent = JsonSerializer.Deserialize<JoinIntent>(InstallFiles.Read(path, DeliveryLimits.SimpleBodyBytes)); }
        catch (Exception ex) when (ex is JsonException or IOException or NotSupportedException or UnauthorizedAccessException) { }
        finally { try { File.Delete(path); consumed = true; } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }
        return consumed && intent is not null && nowUtc >= intent.CreatedUtc && nowUtc - intent.CreatedUtc <= MaxAge ? intent : null;
    }
}
