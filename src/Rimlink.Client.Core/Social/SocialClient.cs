using Rimlink.Client.Net;
using Rimlink.Protocol.V1;

namespace Rimlink.Client.Social;

/// <summary>Block list and the gift/trade flags; every call has a 30 s deadline.</summary>
public sealed class SocialClient(RimlinkConnection connection)
{
    private static DateTime Until => DateTime.UtcNow + Economy.EconomyClient.Deadline;

    public Task<BlockList> Block(long playerId, CancellationToken ct) => connection.SocialApi.BlockAsync(new PlayerRef { PlayerId = playerId }, deadline: Until, cancellationToken: ct).ResponseAsync;
    public Task<BlockList> Unblock(long playerId, CancellationToken ct) => connection.SocialApi.UnblockAsync(new PlayerRef { PlayerId = playerId }, deadline: Until, cancellationToken: ct).ResponseAsync;
    public Task<BlockList> ListBlocks(CancellationToken ct) => connection.SocialApi.ListBlocksAsync(new Empty(), deadline: Until, cancellationToken: ct).ResponseAsync;
    public Task<PlayerFlags> SetAcceptGifts(bool on, CancellationToken ct) => connection.SocialApi.SetAcceptGiftsAsync(new FlagRequest { On = on }, deadline: Until, cancellationToken: ct).ResponseAsync;
    public Task<PlayerFlags> SetTradesClosed(bool on, CancellationToken ct) => connection.SocialApi.SetTradesClosedAsync(new FlagRequest { On = on }, deadline: Until, cancellationToken: ct).ResponseAsync;
}
