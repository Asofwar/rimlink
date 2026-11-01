using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Admin;
using Rimlink.Server.Grpc;
using Rimlink.Server.Rest;
using Role = Rimlink.Server.Storage.Entities.Role;
namespace Rimlink.Server.Application.Procurement;

public static class ProcurementFeature
{
    public static IServiceCollection AddProcurement(this IServiceCollection services)
    {
        services.AddScoped<ProcurementService>();
        services.AddHostedService<ProcurementSweep>();
        return services;
    }

    public sealed record CancelBody(string? Reason);

    public static void MapProcurement(this WebApplication app)
    {
        app.MapGrpcService<ProcurementGrpcService>();
        var group = app.MapGroup("/api/admin/procurement").RequireAdmin();
        group.MapGet("", async (HttpContext ctx, ProcurementService service, int? page) =>
        {
            ctx.Actor().Require(Role.Moderator);
            var result = await service.List(0, new ProcurementQuery { Page = page ?? 0 }, admin: true);
            return Results.Ok(new { result.Total, entries = result.Entries.Select(e => new {
                e.Id, kind = e.Kind.ToString(), buyerId = e.Buyer.PlayerId, buyerName = e.Buyer.Name,
                supplierId = e.Supplier?.PlayerId, e.DefName, e.Quantity, e.Supplied, e.Reward, e.Escrow,
                status = e.Status.ToString(), expiresAt = e.ExpiresAt.ToDateTime(),
                contributions = e.Contributions.Select(c => new { playerId = c.Supplier.PlayerId, name = c.Supplier.Name, c.Quantity, c.Paid }),
            }) });
        }).RequireScope(ApiScope.Moderate);
        group.MapPost("/{id:long}/cancel", async (long id, CancelBody body, HttpContext ctx, ProcurementService service, AdminAudit audit) =>
        {
            var actor = ctx.Actor(); actor.Require(Role.Admin);
            if (body.Reason is { Length: > 200 }) return Results.BadRequest("reason is limited to 200 characters");
            bool closed = await service.CancelAdmin(id);
            if (closed) await audit.Write(actor, "procurement-cancel", null, $"procurement {id}: {body.Reason ?? "cancelled by admin"}");
            return Results.Ok(new { closed });
        }).RequireScope(ApiScope.Admin);
    }
}

public sealed class ProcurementSweep(IServiceScopeFactory scopes, ILogger<ProcurementSweep> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        try
        {
            do
            {
                try
                {
                    using var scope = scopes.CreateScope();
                    var service = scope.ServiceProvider.GetRequiredService<ProcurementService>();
                    await service.ExpireDue(scope.ServiceProvider.GetRequiredService<TimeProvider>().GetUtcNow().UtcDateTime);
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { log.LogError(ex, "Procurement expiry failed; remaining escrow stays held for retry"); }
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
