using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Rimlink.Server.Storage;

namespace Rimlink.Server.Tests.Support;

public sealed record SocialClients(Protocol.V1.Social.SocialClient Social, Protocol.V1.Trades.TradesClient Trades, EconomyClients Economy);

public static class SocialFixtures
{
    public static SocialClients Social(ServerFixture fixture, string token)
    {
        var channel = fixture.CreateChannel();
        return new SocialClients(new Protocol.V1.Social.SocialClient(channel), new Protocol.V1.Trades.TradesClient(channel), EconomyFixtures.Clients(fixture, token));
    }

    public static Protocol.V1.TradeOpenRequest OpenTrade(long partner, Protocol.V1.ItemList? items = null, Protocol.V1.PawnList? pawns = null, long silver = 0, string note = "", long tick = 100, string? requestId = null) => new()
    {
        ClientRequestId = requestId ?? Guid.NewGuid().ToString("N"),
        PartnerId = partner,
        Items = items ?? new Protocol.V1.ItemList(),
        Pawns = pawns ?? new Protocol.V1.PawnList(),
        Silver = silver,
        Note = note,
        GameTick = tick,
    };

    public static Protocol.V1.TradeSideRequest TradeSide(long tradeId, Protocol.V1.ItemList? items = null, Protocol.V1.PawnList? pawns = null, long silver = 0, string note = "", long tick = 100, bool returnGoods = false, string? requestId = null) => new()
    {
        ClientRequestId = requestId ?? Guid.NewGuid().ToString("N"),
        TradeId = tradeId,
        Items = items ?? new Protocol.V1.ItemList(),
        Pawns = pawns ?? new Protocol.V1.PawnList(),
        Silver = silver,
        Note = note,
        GameTick = tick,
        ReturnGoods = returnGoods,
    };

    /// <summary>Gifts to this player arrive without a decision, as before the safe trades spec (for tests about something else).</summary>
    public static async Task AcceptGifts(ServerFixture fixture, long playerId)
    {
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RimlinkDbContext>();
        await db.Players.Where(p => p.Id == playerId).ExecuteUpdateAsync(s => s.SetProperty(p => p.AcceptGifts, true));
    }

    /// <summary>Gives the player's account a role directly (moderators cannot be blocked).</summary>
    public static async Task SetRole(ServerFixture fixture, long playerId, Rimlink.Server.Storage.Entities.Role role)
    {
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RimlinkDbContext>();
        long accountId = await db.Players.Where(p => p.Id == playerId).Select(p => p.AccountId).SingleAsync();
        await db.Accounts.Where(a => a.Id == accountId).ExecuteUpdateAsync(s => s.SetProperty(a => a.Role, role));
    }
}
