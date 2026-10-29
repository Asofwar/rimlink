using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Rimlink.Protocol.V1;
using Rimlink.Server.Storage;

namespace Rimlink.Server.Tests.Support;

public static class IncidentFixtures
{
    /// <summary>A raid server (no newbie or wealth gates) without the kind-event pair cooldown.</summary>
    public static ServerFixture IncidentServer() => RaidFixtures.RaidServer().WithSetting("Incidents:KindCooldownHours", "0");

    public static Protocol.V1.Incidents.IncidentsClient IncidentsOf(ServerFixture fixture) => new(fixture.CreateChannel());

    public static OrderIncidentRequest Order(long target, string entry = "pirates", int level = 2, bool pods = false, string? requestId = null) => new()
    {
        ClientRequestId = requestId ?? Guid.NewGuid().ToString("N"),
        TargetPlayerId = target,
        EntryId = entry,
        Level = level,
        Pods = pods,
    };

    public static async Task SetBalance(ServerFixture fixture, long playerId, long balance)
    {
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RimlinkDbContext>();
        var wallet = await db.Wallets.FirstOrDefaultAsync(w => w.PlayerId == playerId);
        if (wallet is null) db.Wallets.Add(new Rimlink.Server.Storage.Entities.Wallet { PlayerId = playerId, Balance = balance, UpdatedAt = DateTime.UtcNow });
        else wallet.Balance = balance;
        await db.SaveChangesAsync();
    }

    public static async Task SetPeaceful(ServerFixture fixture, long playerId)
    {
        using var scope = fixture.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<RimlinkDbContext>().Players.Where(p => p.Id == playerId)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.Peaceful, true));
    }

    public static ErrorCode? CodeOf(RpcException ex) => ex.ErrorDetail()?.Code;
}
