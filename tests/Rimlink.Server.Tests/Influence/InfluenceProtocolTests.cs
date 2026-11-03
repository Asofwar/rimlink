using Rimlink.Protocol.V1;
using Xunit;

namespace Rimlink.Server.Tests.Influence;

public sealed class InfluenceProtocolTests
{
    [Fact]
    public void ExistingNumbersStayStableAndInfluenceIsAdditive()
    {
        Assert.Equal(60, (int)ErrorCode.SaveGenerationMismatch);
        Assert.Equal(61, (int)ErrorCode.InfluenceConflict);
        Assert.Equal(66, (int)ErrorCode.InfluenceUnavailable);
        Assert.Equal(111, ServerEvent.Descriptor.FindFieldByName("influence_changed").FieldNumber);
        Assert.Equal(6, WorldInfo.Descriptor.FindFieldByName("world_instance_id").FieldNumber);
        Assert.Equal(12, LoginReply.Descriptor.FindFieldByName("save_generation").FieldNumber);
        Assert.Equal(6, ColonyRestore.Descriptor.FindFieldByName("influence_notice_key").FieldNumber);
    }

    [Fact]
    public void FalseIsAnExplicitIntentNotMissingConsentOrLayer()
    {
        var publication = new InfluencePublication();
        Assert.False(publication.HasEnabled);
        publication.Enabled = false;
        Assert.True(publication.HasEnabled);
        var surface = new InfluenceSurface { RootSurface = false };
        Assert.True(surface.HasRootSurface);
        Assert.False(surface.RootSurface);
        Assert.False(new InfluenceSetting().HasRootSurface);
    }
}
