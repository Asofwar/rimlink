using Rimlink.Client.Net;
using Rimlink.Protocol.V1;
using Xunit;

namespace Rimlink.Client.Tests.Net;

public class SteamLoginWaitTests
{
    private static List<TimeSpan> AllDelays(SteamLoginWait wait)
    {
        var delays = new List<TimeSpan>();
        while (wait.Next(ErrorCode.SteamTicketRejected) is { } delay) delays.Add(delay);
        return delays;
    }

    [Fact]
    public void ARejectedTicketIsRetriedForAboutTwoAndAHalfMinutes()
    {
        var delays = AllDelays(new SteamLoginWait());

        Assert.Equal(2, delays[0].TotalSeconds);
        Assert.All(delays, d => Assert.True(d <= TimeSpan.FromSeconds(15)));
        var total = TimeSpan.FromTicks(delays.Sum(d => d.Ticks));
        Assert.InRange(total, TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(3)); // Steam took about two minutes on 2026-10-30
    }

    [Theory]
    [InlineData(null)]                         // a server without a Steam key: the account login is the way in
    [InlineData(ErrorCode.Unspecified)]
    [InlineData(ErrorCode.Banned)]
    public void AnyOtherRefusalIsNotRetried(ErrorCode? code) =>
        Assert.Null(new SteamLoginWait().Next(code));

    [Fact]
    public void AfterGivingUpItStaysGivenUp()
    {
        var wait = new SteamLoginWait();
        AllDelays(wait);

        Assert.Null(wait.Next(ErrorCode.SteamTicketRejected));
    }
}
