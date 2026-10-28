using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Rimlink.Protocol.V1;
using Rimlink.Server.Storage;

namespace Rimlink.Server.Tests.Support;

public sealed record RaidClients(Protocol.V1.Raids.RaidsClient Raids, Protocol.V1.Visits.VisitsClient Visits, Protocol.V1.Shop.ShopClient Shop,
    Protocol.V1.Mailbox.MailboxClient Mailbox, Protocol.V1.Wallet.WalletClient Wallet, Protocol.V1.Saves.SavesClient Saves, Metadata Auth);

public static class RaidFixtures
{
    /// <summary>Economy server where a target with a save can be raided at once (protection rules relaxed; see <see cref="MakeTargetRaidable"/>).</summary>
    public static ServerFixture RaidServer() => EconomyFixtures.EconomyServer().WithSetting("Raids:NewbieProtectionDays", "0").WithSetting("Raids:MinColonyWealth", "0");

    public static RaidClients RaidClientsOf(ServerFixture fixture, string token)
    {
        var channel = fixture.CreateChannel();
        return new RaidClients(new Protocol.V1.Raids.RaidsClient(channel), new Protocol.V1.Visits.VisitsClient(channel), new Protocol.V1.Shop.ShopClient(channel),
            new Protocol.V1.Mailbox.MailboxClient(channel), new Protocol.V1.Wallet.WalletClient(channel), new Protocol.V1.Saves.SavesClient(channel), GrpcTestExtensions.Bearer(token));
    }

    public static LaunchRequest Launch(long target, int pawns = 2, PawnMode mode = PawnMode.PawnReal, RaidTiming timing = RaidTiming.TimingWarned,
        RaidArrival arrival = RaidArrival.ArrivalEdge, long tick = 100, string? requestId = null)
    {
        var request = new LaunchRequest
        {
            ClientRequestId = requestId ?? Guid.NewGuid().ToString("N"),
            TargetPlayerId = target,
            PawnMode = mode,
            Timing = timing,
            Arrival = arrival,
            GameTick = tick,
            Pawns = new PawnList(),
        };
        for (int i = 0; i < pawns; i++) request.Pawns.Pawns.Add(EconomyFixtures.Pawn("Colonist", "Raider" + i));
        return request;
    }

    /// <summary>A colony old and rich enough to be raided: first save ten days ago, wealth as given.</summary>
    public static async Task MakeTargetRaidable(ServerFixture fixture, long playerId, long wealthItems = 100000, long wealthTotal = 100000)
    {
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RimlinkDbContext>();
        await db.Players.Where(p => p.Id == playerId).ExecuteUpdateAsync(s => s
            .SetProperty(p => p.FirstSaveAt, DateTime.UtcNow - TimeSpan.FromDays(10))
            .SetProperty(p => p.WealthItems, wealthItems)
            .SetProperty(p => p.WealthTotal, wealthTotal));
    }
}
