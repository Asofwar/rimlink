using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace Rimlink.Server.Hosting;

public static class KestrelSetup
{
    /// <summary>
    /// One TLS endpoint on <see cref="ServerOptions.Port"/> with the spec's keep-alive timings. ALPN gives gRPC clients
    /// HTTP/2; HTTP/1.1 stays on for the admin REST API (CLI, curl, browsers) and the health check.
    /// </summary>
    public static void Configure(KestrelServerOptions kestrel, ServerOptions server, TlsOptions tls)
    {
        X509Certificate2 certificate = LoadCertificate(server, tls);

        kestrel.Limits.Http2.KeepAlivePingDelay = TimeSpan.FromSeconds(30);
        kestrel.Limits.Http2.KeepAlivePingTimeout = TimeSpan.FromSeconds(10);
        kestrel.ListenAnyIP(server.Port, listen =>
        {
            listen.Protocols = HttpProtocols.Http1AndHttp2;
            UnauthenticatedConnectionLimiter.Install(listen);
            listen.UseHttps(certificate);
        });
    }

    private static X509Certificate2 LoadCertificate(ServerOptions server, TlsOptions tls)
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
