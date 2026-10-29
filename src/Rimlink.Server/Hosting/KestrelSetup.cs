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
        var certificate = new ServerCertificate(server, tls).Certificate;

        kestrel.Limits.Http2.KeepAlivePingDelay = TimeSpan.FromSeconds(30);
        kestrel.Limits.Http2.KeepAlivePingTimeout = TimeSpan.FromSeconds(10);
        kestrel.ListenAnyIP(server.Port, listen =>
        {
            listen.Protocols = HttpProtocols.Http1AndHttp2;
            UnauthenticatedConnectionLimiter.Install(listen);
            listen.UseHttps(certificate);
        });
    }
}
