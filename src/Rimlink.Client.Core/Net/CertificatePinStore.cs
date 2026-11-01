using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace Rimlink.Client.Net;

public enum PinStatus { Trusted, FirstSeen, Changed }

public sealed record PinCheck(PinStatus Status, string Fingerprint, string? Previous);

/// <summary>Remembers each server's certificate fingerprint (SHA-256 of DER, hex), like SSH known_hosts.</summary>
public sealed class CertificatePinStore
{
    private readonly string path;
    private readonly Dictionary<string, string> pins;
    private static readonly object sync = new();

    public CertificatePinStore(string path)
    {
        this.path = path;
        lock (sync) pins = Load(path);
    }

    public static string Fingerprint(X509Certificate2 certificate) => Convert.ToHexString(SHA256.HashData(certificate.RawData));

    public string? KnownFingerprint(ServerEndpoint endpoint)
    {
        lock (sync) return pins.GetValueOrDefault(endpoint.Id);
    }

    /// <summary>Import is deliberately unable to replace an existing pin, including between preview and confirmation.</summary>
    public bool TryTrustInvite(ServerInvite invite)
    {
        lock (sync)
        {
            // A preview can outlive another connection's trust change. A broken file must fail closed on import.
            var next = File.Exists(path) ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path))
                ?? throw new JsonException("Invalid trust file.") : new Dictionary<string, string>();
            if (pins.TryGetValue(invite.Endpoint.Id, out var cached) && cached != invite.Fingerprint) return false;
            if (next.TryGetValue(invite.Endpoint.Id, out var known) && known != invite.Fingerprint) return false;
            next[invite.Endpoint.Id] = invite.Fingerprint;
            Persist(next);
            pins[invite.Endpoint.Id] = invite.Fingerprint;
            return true;
        }
    }

    public PinCheck Check(ServerEndpoint endpoint, X509Certificate2 certificate)
    {
        string fingerprint = Fingerprint(certificate);
        lock (sync)
        {
            if (!pins.TryGetValue(endpoint.Id, out var known)) return new PinCheck(PinStatus.FirstSeen, fingerprint, null);
            return known == fingerprint ? new PinCheck(PinStatus.Trusted, fingerprint, known) : new PinCheck(PinStatus.Changed, fingerprint, known);
        }
    }

    public void Save(ServerEndpoint endpoint, string fingerprint)
    {
        lock (sync)
        {
            var next = Load(path);
            next[endpoint.Id] = fingerprint;
            Persist(next);
            pins[endpoint.Id] = fingerprint;
        }
    }

    private void Persist(Dictionary<string, string> next)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(next, new JsonSerializerOptions { WriteIndented = true }), new System.Text.UTF8Encoding(false));
            File.Move(temp, path, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private static Dictionary<string, string> Load(string path)
    {
        try
        {
            if (File.Exists(path)) return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? [];
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // A broken pin file means "nothing pinned"; the user is asked again.
        }
        return [];
    }
}
