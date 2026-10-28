using System.Net;
using Rimlink.Server.Hosting;
using Xunit;

namespace Rimlink.Server.Tests.Hosting;

public class ClientAddressTests
{
    [Theory]
    [InlineData("1.2.3.4", "1.2.3.4")]
    [InlineData("::ffff:1.2.3.4", "1.2.3.4")] // IPv4 client on a dual-stack listener
    [InlineData("2001:db8::1", "2001:db8::1")]
    public void NormalizesMappedAddresses(string raw, string expected)
    {
        Assert.Equal(expected, ClientAddress.Normalize(IPAddress.Parse(raw)));
    }

    [Theory]
    [InlineData("[::ffff:1.2.3.4]:51000", "1.2.3.4")]
    [InlineData("[2001:db8::1]:51000", "2001:db8::1")]
    [InlineData("1.2.3.4:51000", "1.2.3.4")]
    public void NormalizesEndpoints(string endpoint, string expected)
    {
        Assert.Equal(expected, ClientAddress.Normalize(IPEndPoint.Parse(endpoint)));
    }

    [Fact]
    public void NullIsUnknown()
    {
        Assert.Equal("unknown", ClientAddress.Normalize((EndPoint?)null));
    }
}
