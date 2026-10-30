using Rimlink.Client.Net;
using Xunit;

namespace Rimlink.Client.Tests.Net;

public class SteamTicketReuseTests
{
    [Fact]
    public void AnUnconfirmedTicketIsKeptSoThatItCanMature() =>
        Assert.True(SteamTicketReuse.Keep(confirmed: false, TimeSpan.FromSeconds(10), sentConfirmed: false));

    [Fact]
    public void ATicketSteamNeverConfirmedIsReplacedInTheEnd() =>
        Assert.False(SteamTicketReuse.Keep(confirmed: false, SteamTicketReuse.MaxUnconfirmedAge, sentConfirmed: false));

    [Fact]
    public void ATicketConfirmedAfterItWasSentGoesOnceMore() =>
        Assert.True(SteamTicketReuse.Keep(confirmed: true, TimeSpan.FromMinutes(5), sentConfirmed: false));

    [Fact]
    public void AConfirmedTicketThatAlreadyWentIsReplaced() =>
        Assert.False(SteamTicketReuse.Keep(confirmed: true, TimeSpan.FromSeconds(1), sentConfirmed: true));
}
