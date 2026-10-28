using Rimlink.Client.Net;
using Xunit;

namespace Rimlink.Client.Tests.Net;

public class ReconnectPolicyTests
{
    [Fact]
    public void DelaysDoubleUpToSixtyAndFitInTwoMinutes()
    {
        var delays = ReconnectPolicy.Delays().Select(d => (int)d.TotalSeconds).ToList();

        Assert.Equal([1, 2, 4, 8, 16, 32, 57], delays);
        Assert.Equal(120, delays.Sum());
        Assert.Equal(TimeSpan.FromSeconds(120), ReconnectPolicy.Deadline);
    }
}
