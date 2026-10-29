using Rimlink.Client.Raids;
using Rimlink.Protocol.V1;
using Xunit;

namespace Rimlink.Client.Tests.Raids;

public class RaidsStateTests
{
    [Fact]
    public void ExpiredEventDropsAnIncomingRaid()
    {
        // A raid cancelled or expired before it landed must stop closing the target's economy (economy hardening spec §3).
        var state = new RaidsState();
        state.AddIncoming(new Raid { Id = 7 });
        state.AddIncoming(new Raid { Id = 8 });

        state.Apply(new ServerEvent { RaidExpired = new RaidExpired { RaidId = 7 } });

        Assert.Equal([8L], state.Incoming.Select(r => r.Id).ToArray());
    }
}
