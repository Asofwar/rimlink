using Rimlink.Client.Economy;
using Rimlink.Protocol.V1;
using Xunit;

namespace Rimlink.Client.Tests.Economy;

public class GiftStateTests
{
    private static ServerEvent Arrived(long id, bool awaiting) =>
        new() { ParcelArrived = new ParcelArrived { Parcel = new Parcel { Id = id, AwaitingDecision = awaiting } } };

    [Fact]
    public void AcceptedGiftReplacesTheAwaitingOne()
    {
        var state = new EconomyState();
        int raised = 0;
        state.ParcelArrived += _ => raised++;

        state.Apply(Arrived(3, awaiting: true));
        state.Apply(Arrived(3, awaiting: false));

        Assert.False(Assert.Single(state.Pending).AwaitingDecision);
        Assert.Equal(1, raised);
    }

    [Fact]
    public void RefusedGiftLeavesThePending()
    {
        var state = new EconomyState();
        state.Apply(Arrived(3, awaiting: true));
        state.Apply(Arrived(4, awaiting: false));

        state.Apply(new ServerEvent { GiftRefused = new GiftRefused { ParcelId = 3 } });

        Assert.Equal(4, Assert.Single(state.Pending).Id);
    }
}
