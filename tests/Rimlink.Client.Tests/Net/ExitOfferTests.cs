using Rimlink.Client.Net;
using Xunit;

namespace Rimlink.Client.Tests.Net;

public class ExitOfferTests
{
    [Fact]
    public void TheOfferComesOncePerLongOutage()
    {
        var offer = new ExitOffer();
        var outage = new DateTime(2026, 10, 29, 12, 0, 0, DateTimeKind.Utc);

        Assert.False(offer.Due(outage, outageIsLong: false));
        Assert.True(offer.Due(outage, outageIsLong: true));
        Assert.False(offer.Due(outage, outageIsLong: true));
        Assert.False(offer.Due(null, outageIsLong: false));
    }

    [Fact]
    public void ALaterOutageIsOfferedAgainEvenAfterTheFirstOfferWasNeverClosed()
    {
        // The player left to the menu from the offer itself and logged in again in the same game: a new connection, a new outage.
        var offer = new ExitOffer();
        var first = new DateTime(2026, 10, 29, 12, 0, 0, DateTimeKind.Utc);
        Assert.True(offer.Due(first, outageIsLong: true));

        Assert.True(offer.Due(first.AddMinutes(30), outageIsLong: true));
    }
}
