using Rimlink.Protocol.V1;

namespace Rimlink.Client.Social;

/// <summary>The player's trades (open and recently closed) and block list. Loaded at login, fed by the event stream.</summary>
public sealed class SocialState
{
    private readonly List<Trade> trades = [];
    private readonly Dictionary<long, PlayerRef> blocks = [];

    /// <summary>Newest first.</summary>
    public IReadOnlyList<Trade> Trades => trades;
    public IReadOnlyCollection<PlayerRef> Blocks => blocks.Values;

    public event Action<Trade>? TradeUpdated;
    /// <summary>A gift of ours was refused (or one addressed to us was refused, by us or by the sweep).</summary>
    public event Action<GiftRefused>? GiftRefused;

    public bool IsBlocked(long playerId) => blocks.ContainsKey(playerId);

    public void Load(TradeList list, BlockList blockList)
    {
        trades.Clear();
        trades.AddRange(list.Trades.OrderByDescending(t => t.Id));
        SetBlocks(blockList);
    }

    public void SetBlocks(BlockList blockList)
    {
        blocks.Clear();
        foreach (var p in blockList.Players) blocks[p.PlayerId] = p;
    }

    public void Upsert(Trade trade)
    {
        int at = trades.FindIndex(t => t.Id == trade.Id);
        if (at >= 0)
        {
            trades[at] = trade;
            return;
        }
        trades.Add(trade);
        trades.Sort((a, b) => b.Id.CompareTo(a.Id));
    }

    public void Apply(ServerEvent ev)
    {
        switch (ev.BodyCase)
        {
            case ServerEvent.BodyOneofCase.TradeUpdated:
                Upsert(ev.TradeUpdated.Trade);
                TradeUpdated?.Invoke(ev.TradeUpdated.Trade);
                break;
            case ServerEvent.BodyOneofCase.GiftRefused:
                GiftRefused?.Invoke(ev.GiftRefused);
                break;
        }
    }

    public void Reset()
    {
        trades.Clear();
        blocks.Clear();
    }
}
