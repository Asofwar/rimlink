using Microsoft.Extensions.Options;
using Rimlink.Server.Application.Admin;
using Rimlink.Server.Hosting;

namespace Rimlink.Server.Rest;

/// <summary>The public page's data: no login, no IPs, SteamIDs or account ids (admin panel spec §3, §4).</summary>
public static class PublicEndpoints
{
    public static void MapPublicApi(this WebApplication app)
    {
        app.MapGet("/api/public/status", async (HttpContext ctx, IOptions<PanelOptions> panel, IOptions<ServerOptions> server, PublicStatusCache cache, ServerStatus status) =>
        {
            if (!panel.Value.Enabled || !panel.Value.Public) return Results.NotFound();
            var current = await cache.Get(status.Public);
            string address = string.IsNullOrWhiteSpace(panel.Value.PublicAddress) ? $"{ctx.Request.Host.Host}:{server.Value.Port}" : panel.Value.PublicAddress.Trim();
            return Results.Ok(current with { Address = address });
        });
    }
}
