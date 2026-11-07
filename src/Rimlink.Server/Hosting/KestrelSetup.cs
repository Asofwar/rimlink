using Microsoft.AspNetCore.Server.Kestrel.Core;
using System.Net;

namespace Rimlink.Server.Hosting;

public static class KestrelSetup
{
    /// <summary>
    /// One TLS endpoint on <see cref="ServerOptions.Port"/> with the spec's keep-alive timings. ALPN gives gRPC clients
    /// HTTP/2; HTTP/1.1 stays on for the admin REST API (CLI, curl, browsers) and the health check.
    /// </summary>
    public static void Configure(KestrelServerOptions kestrel, ServerOptions server, TlsOptions tls)
        => Configure(kestrel, server, tls, loopback: false);

    internal static void ConfigureLoopback(KestrelServerOptions kestrel, ServerOptions server, TlsOptions tls)
        => Configure(kestrel, server, tls, loopback: true);

    private static void Configure(KestrelServerOptions kestrel, ServerOptions server, TlsOptions tls, bool loopback)
    {
        var certificate = new ServerCertificate(server, tls).Certificate;

        kestrel.Limits.Http2.KeepAlivePingDelay = TimeSpan.FromSeconds(30);
        kestrel.Limits.Http2.KeepAlivePingTimeout = TimeSpan.FromSeconds(10);
        void Endpoint(ListenOptions listen)
        {
            listen.Protocols = HttpProtocols.Http1AndHttp2;
            UnauthenticatedConnectionLimiter.Install(listen);
            listen.UseHttps(certificate);
        }
        if (loopback) kestrel.Listen(IPAddress.Loopback, server.Port, Endpoint);
        else kestrel.ListenAnyIP(server.Port, Endpoint);
    }
}
