using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Options;

namespace Rimlink.Server.Hosting;

/// <summary>Refuses to run without an HTTPS endpoint unless <see cref="TlsOptions.Required"/> is off (tests).</summary>
public sealed class TlsStartupCheck(IServer server, IOptions<TlsOptions> tls, ILogger<TlsStartupCheck> log) : IHostedLifecycleService
{
    // Runs after every hosted service's StartAsync, including the web server's, so the bound addresses are known.
    public Task StartedAsync(CancellationToken cancellationToken)
    {
        if (!tls.Value.Required)
        {
            log.LogWarning("TLS enforcement is disabled (Tls:Required=false); never run a public server this way");
            return Task.CompletedTask;
        }

        var addresses = server.Features.Get<IServerAddressesFeature>()?.Addresses ?? [];
        if (!addresses.Any(a => a.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("TLS is required but no HTTPS endpoint is configured");
        }

        log.LogInformation("TLS endpoint check passed: {Addresses}", string.Join(", ", addresses));
        return Task.CompletedTask;
    }

    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
