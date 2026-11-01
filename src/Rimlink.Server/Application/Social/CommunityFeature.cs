using Microsoft.Extensions.Options;
using Rimlink.Server.Application.Admin;
using Rimlink.Server.Grpc;
using Rimlink.Server.Hosting;
using Rimlink.Server.Rest;

namespace Rimlink.Server.Application.Social;

public static class CommunityFeature
{
    // Explicit REST DTO shared by public and moderation routes; protobuf implementation details never escape.
    public sealed record ProfileView(long PlayerId, string Name, string Bio, DateTime? LastSeen, bool InvitationsOpen, bool Notifications);

    public static IServiceCollection AddCommunity(this IServiceCollection services) => services.AddScoped<AllianceService>();

    public static void MapCommunity(this WebApplication app)
    {
        app.MapGrpcService<CommunityGrpcService>();
        app.MapGet("/api/public/profiles/{id:long}", async (long id, AllianceService social, IOptions<PanelOptions> panel, CancellationToken ct) =>
        {
            if (!panel.Value.Enabled || !panel.Value.Public) return Results.NotFound();
            try { return Results.Ok(await View(social, id, ct)); }
            catch (global::Grpc.Core.RpcException) { return Results.NotFound(); }
        });
        app.MapGroup("/api/admin").RequireAdmin().MapGet("/profiles/{id:long}", async (long id, AllianceService social, CancellationToken ct) =>
            await View(social, id, ct)).RequireScope(ApiScope.Moderate);
    }

    private static async Task<ProfileView> View(AllianceService social, long id, CancellationToken ct)
    {
        var p = await social.Profile(id, ct);
        return new ProfileView(p.PlayerId, p.Name, p.Bio, p.LastSeen?.ToDateTime(), p.InvitationsOpen, p.Notifications);
    }
}
