using Microsoft.EntityFrameworkCore;
using Rimlink.Server.Application.Admin;
using Rimlink.Server.Grpc;
using Rimlink.Server.Rest;
using Rimlink.Server.Storage;
using Rimlink.Server.Storage.Entities;

namespace Rimlink.Server.Application.Auctions;

public static class AuctionFeature
{
    public static IServiceCollection AddAuctions(this IServiceCollection services) => services.AddScoped<AuctionService>().AddScoped<AuctionAdmin>().AddHostedService<AuctionSweep>();
    public static void MapAuctions(this WebApplication app)
    {
        app.MapGrpcService<AuctionsGrpcService>();
        var group = app.MapGroup("/api/admin/auctions").RequireAdmin();
        group.MapGet("/", (HttpContext ctx, AuctionAdmin admin) => admin.List(ctx.Actor())).RequireScope(ApiScope.Moderate);
        group.MapPost("/{id:long}/cancel", (long id, AuctionCancelBody body, HttpContext ctx, AuctionAdmin admin) => admin.Cancel(ctx.Actor(), id, body.Reason)).RequireScope(ApiScope.Moderate);
    }
    public sealed record AuctionCancelBody(string? Reason);
}

public sealed class AuctionAdmin(AuctionService auctions, RimlinkDbContext db, AdminAudit audit)
{
    public async Task<object[]> List(Actor actor)
    {
        actor.Require(Role.Moderator);
        return (await db.Set<AuctionRow>().AsNoTracking().Where(a => a.Status == 0).OrderBy(a => a.Id).Take(500)
            .Select(a => new { a.Id, a.SellerPlayerId, a.StartPrice, a.BuyoutPrice, a.Bid, a.BidderPlayerId, a.ExpiresAt }).ToListAsync()).Cast<object>().ToArray();
    }
    public async Task<bool> Cancel(Actor actor, long id, string? reason)
    {
        actor.Require(Role.Moderator);
        bool result = await auctions.AdminCancel(id, reason);
        if (result) await audit.Write(actor, "auction_cancel", null, $"auction {id}: {reason}");
        return result;
    }
}

public sealed class AuctionSweep(IServiceScopeFactory scopes, TimeProvider time, ILogger<AuctionSweep> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        try
        {
            do
            {
                try
                {
                    using var scope = scopes.CreateScope();
                    await scope.ServiceProvider.GetRequiredService<AuctionService>().ExpireDue(time.GetUtcNow().UtcDateTime);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested) { log.LogWarning(ex, "Auction sweep failed; retrying"); }
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
