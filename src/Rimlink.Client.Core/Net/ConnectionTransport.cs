using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace Rimlink.Client.Net;

/// <summary>Where to connect and through which HTTP handler. Tests inject an in-memory handler; the game uses <see cref="Pinned"/>.</summary>
public sealed record ConnectionTransport(Uri Address, HttpMessageHandler Handler)
{
    /// <summary>
    /// HTTP/2 over TLS with certificate pinning: a certificate whose fingerprint is not stored for the endpoint fails the
    /// handshake and is reported through <paramref name="unknown"/>, so the caller can ask the user and retry.
    /// </summary>
    public static ConnectionTransport Pinned(ServerEndpoint endpoint, CertificatePinStore pins, Action<PinCheck> unknown)
    {
        var handler = new SocketsHttpHandler
        {
            EnableMultipleHttp2Connections = false,
            KeepAlivePingDelay = TimeSpan.FromSeconds(30),
            KeepAlivePingTimeout = TimeSpan.FromSeconds(10),
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                {
                    if (certificate is null) return false;
                    using var cert = new X509Certificate2(certificate);
                    var check = pins.Check(endpoint, cert);
                    if (check.Status == PinStatus.Trusted) return true;
                    unknown(check);
                    return false;
                },
            },
        };
        return new ConnectionTransport(endpoint.Address, handler);
    }
}
