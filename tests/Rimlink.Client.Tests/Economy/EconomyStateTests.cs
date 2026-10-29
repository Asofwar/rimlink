using Rimlink.Client.Economy;
using Rimlink.Protocol.V1;
using Xunit;

namespace Rimlink.Client.Tests.Economy;

/// <summary>The client keeps server-held wealth, the economy rules and debt (economy hardening spec §2, §4, §5).</summary>
public class EconomyStateTests
{
    private static EconomyState Loaded(long balance = 5, long wealth = 900, params string[] forbidden)
    {
        var state = new EconomyState();
        var rules = new EconomyRules { StorageFeePercentPerDay = 0.5, StorageFreeDays = 3 };
        rules.ForbiddenDefs.AddRange(forbidden);
        state.Load(new WalletState { Balance = balance, ServerWealth = wealth }, new ParcelList(), new LotList(), rules);
        return state;
    }

    [Fact]
    public void LoadKeepsWealthAndRules()
    {
        var state = Loaded(forbidden: "Gun_Revolver");

        Assert.Equal(900, state.ServerWealth);
        Assert.True(state.IsForbidden("Gun_Revolver"));
        Assert.False(state.IsForbidden("Steel"));
        Assert.Equal(0.5, state.Rules.StorageFeePercentPerDay);
        Assert.False(state.WealthStale);
    }

    [Fact]
    public void RulesAreNeverNull()
    {
        var state = new EconomyState();
        state.Load(new WalletState(), new ParcelList(), new LotList());

        Assert.NotNull(state.Rules);
        Assert.False(state.IsForbidden("Steel"));
    }

    [Theory]
    [InlineData(ServerEvent.BodyOneofCase.ParcelArrived)]
    [InlineData(ServerEvent.BodyOneofCase.LotSold)]
    [InlineData(ServerEvent.BodyOneofCase.LotClosed)]
    [InlineData(ServerEvent.BodyOneofCase.WalletChanged)]
    public void EventsMarkWealthStaleAndSetWalletClearsIt(ServerEvent.BodyOneofCase body)
    {
        var state = Loaded();
        var ev = body switch
        {
            ServerEvent.BodyOneofCase.ParcelArrived => new ServerEvent { ParcelArrived = new ParcelArrived { Parcel = new Parcel { Id = 3 } } },
            ServerEvent.BodyOneofCase.LotSold => new ServerEvent { LotSold = new LotSold { LotId = 4 } },
            ServerEvent.BodyOneofCase.LotClosed => new ServerEvent { LotClosed = new LotClosed { LotId = 4 } },
            _ => new ServerEvent { WalletChanged = new WalletChanged { Balance = 7 } },
        };

        state.Apply(ev);
        Assert.True(state.WealthStale);

        state.SetWallet(new WalletState { Balance = 8, ServerWealth = 1200 });
        Assert.False(state.WealthStale);
        Assert.Equal(8, state.Balance);
        Assert.Equal(1200, state.ServerWealth);
    }

    [Fact]
    public void NegativeBalanceIsDebt()
    {
        var state = Loaded();

        state.Apply(new ServerEvent { WalletChanged = new WalletChanged { Balance = -1, Delta = -6, Kind = "storage" } });

        Assert.True(state.InDebt);
    }
}
