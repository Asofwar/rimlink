using Rimlink.Client.Social;
using Rimlink.Client.State;
using Rimlink.Protocol.V1;
using Xunit;

namespace Rimlink.Client.Tests.Social;

public class SocialStateTests
{
    private static Trade Trade(long id, int revision) => new() { Id = id, Revision = revision, Status = TradeStatus.TradeOpen };

    private static ServerEvent Updated(Trade trade) => new() { TradeUpdated = new TradeUpdated { Trade = trade } };

    [Fact]
    public void TradeUpdatedReplacesByIdAndRaises()
    {
        var state = new SharedState();
        var raised = new List<Trade>();
        state.Social.TradeUpdated += raised.Add;
        var list = new TradeList();
        list.Trades.Add(Trade(1, 1));
        state.Social.Load(list, new BlockList());

        state.Apply(Updated(Trade(1, 2)));

        Assert.Equal(2, Assert.Single(state.Social.Trades).Revision);
        Assert.Single(raised);

        state.Apply(Updated(Trade(2, 1)));

        Assert.Equal([2L, 1L], state.Social.Trades.Select(t => t.Id));
        Assert.Equal(2, raised.Count);
    }

    [Fact]
    public void BlocksAnswerIsBlocked()
    {
        var state = new SharedState();
        var blocks = new BlockList();
        blocks.Players.Add(new PlayerRef { PlayerId = 7, Name = "Bob" });

        state.Social.SetBlocks(blocks);

        Assert.True(state.Social.IsBlocked(7));
        Assert.False(state.Social.IsBlocked(8));
        state.Apply(Updated(Trade(1, 1)));
        state.Reset();
        Assert.Empty(state.Social.Trades);
        Assert.Empty(state.Social.Blocks);
        Assert.False(state.Social.IsBlocked(7));
    }

    [Fact]
    public void GiftRefusedIsRaised()
    {
        var state = new SharedState();
        GiftRefused? got = null;
        state.Social.GiftRefused += g => got = g;

        state.Apply(new ServerEvent { GiftRefused = new GiftRefused { ParcelId = 5, By = new PlayerRef { Name = "Bob" } } });

        Assert.Equal(5, got?.ParcelId);
    }
}
