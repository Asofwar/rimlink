using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Rimlink.Discord.Options;

namespace Rimlink.Discord.Api;

/// <summary>How the bot trusts the server's TLS certificate: the normal chain check, a pinned SHA-256, or nothing (<c>Insecure</c>).</summary>
public static class ServerHandler
{
    public static bool Accepts(ServerSection server, X509Certificate2? certificate, SslPolicyErrors errors)
    {
        if (errors == SslPolicyErrors.None || server.Insecure) return true;
        if (certificate is null || string.IsNullOrEmpty(server.CertificateSha256)) return false;
        return string.Equals(Convert.ToHexString(SHA256.HashData(certificate.RawData)), server.CertificateSha256, StringComparison.OrdinalIgnoreCase);
    }

    public static SocketsHttpHandler Create(ServerSection server) => new()
    {
        SslOptions = { RemoteCertificateValidationCallback = (_, cert, _, errors) => Accepts(server, cert as X509Certificate2, errors) },
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    };
}
