using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Rimlink.Server.Hosting;

/// <summary>
/// The server's TLS certificate: the PEM pair from <see cref="TlsOptions.CertificatePath"/> or the generated self-signed one.
/// Kestrel and the status endpoints each build one from the same options and read the same file, so they agree on the fingerprint.
/// </summary>
public sealed class ServerCertificate(ServerOptions server, TlsOptions tls)
{
    private readonly Lazy<X509Certificate2> certificate = new(() => Load(server, tls));

    public X509Certificate2 Certificate => certificate.Value;

    /// <summary>Uppercase hex SHA-256 of the DER bytes, the format the client shows when it pins a server.</summary>
    public string Fingerprint => Convert.ToHexString(SHA256.HashData(Certificate.RawData));

    public bool SelfSigned => string.IsNullOrWhiteSpace(tls.CertificatePath);

    private static X509Certificate2 Load(ServerOptions server, TlsOptions tls)
    {
        if (!string.IsNullOrWhiteSpace(tls.CertificatePath))
        {
            string keyPath = string.IsNullOrWhiteSpace(tls.KeyPath) ? tls.CertificatePath : tls.KeyPath;
            using var pem = X509Certificate2.CreateFromPemFile(tls.CertificatePath, keyPath);
            // Re-import so the private key is usable by SslStream on Windows.
            return X509CertificateLoader.LoadPkcs12(pem.Export(X509ContentType.Pfx), password: null);
        }

        string pfxPath = string.IsNullOrWhiteSpace(tls.SelfSignedPath)
            ? Path.Combine(server.DataDir, "tls", "server.pfx")
            : tls.SelfSignedPath;
        return SelfSignedCertificate.LoadOrCreate(pfxPath);
    }
}
