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
    private readonly object sync = new();

    public CertificatePinStore(string path)
    {
        this.path = path;
        pins = Load(path);
    }

    public static string Fingerprint(X509Certificate2 certificate) => Convert.ToHexString(SHA256.HashData(certificate.RawData));

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
            pins[endpoint.Id] = fingerprint;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(pins, new JsonSerializerOptions { WriteIndented = true }));
        }
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
