using Rimlink.Protocol;
using Xunit;

namespace Rimlink.Client.Tests;

public class ClientInfoTests
{
    [Fact]
    public void ClientSpeaksTheSharedProtocolVersion()
    {
        Assert.Equal(ProtocolVersion.Current, ClientInfo.ProtocolVersion);
        Assert.Equal("asofwar.rimlink", ClientInfo.ModPackageId);
    }
}
