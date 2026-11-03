using Rimlink.Client.Mods.Delivery;
using Rimlink.Protocol.V1;
using Xunit;

namespace Rimlink.Client.Tests.Mods;

public sealed class ModpackJoinReadinessTests
{
    [Theory]
    [InlineData(true, Role.Player, true, true)]
    [InlineData(false, Role.Player, true, false)]
    [InlineData(false, Role.Player, false, false)]
    [InlineData(false, Role.Admin, true, false)]
    [InlineData(false, Role.Admin, false, true)]
    public void FailedDeliveryVerificationBlocksRejoinButKeepsExplicitManualAdminBypass(bool verified, Role role, bool automatic, bool mayContinue) =>
        Assert.Equal(mayContinue, ModpackJoinReadiness.MayContinue(verified, role, automatic));
}
