using Google.Protobuf;
using Rimlink.Protocol.V1;
using Rimlink.Server.Tests.Support;
using Xunit;
using static Rimlink.Server.Tests.Support.GrpcTestExtensions;

namespace Rimlink.Server.Tests.World;

public class StorytellerLockTests
{
    private static ServerFixture Server() => new ServerFixture()
        .WithSetting("Auth:AllowPassword", "true")
        .WithSetting("Auth:AdminLogins:0", "admin");

    [Fact]
    public async Task WorldInfoCarriesThePolicy()
    {
        using (var fixture = Server())
        {
            var nova = await fixture.RegisterAndLogin("nova");
            var info = await new Protocol.V1.World.WorldClient(fixture.CreateChannel()).GetAsync(new Empty(), Bearer(nova.SessionToken));
            Assert.True(info.StorytellerLocked);
        }
        using (var free = Server().WithSetting("Integrity:StorytellerPolicy", "Free"))
        {
            var nova = await free.RegisterAndLogin("nova");
            var info = await new Protocol.V1.World.WorldClient(free.CreateChannel()).GetAsync(new Empty(), Bearer(nova.SessionToken));
            Assert.False(info.StorytellerLocked);
        }
    }

    [Fact]
    public async Task CreatedWorldIsLockedAndKeepsTheDifficultyValues()
    {
        using var fixture = Server().WithoutWorld();
        var admin = await fixture.RegisterAndLogin("admin");
        var world = new Protocol.V1.World.WorldClient(fixture.CreateChannel());
        var values = ByteString.CopyFrom([1, 2, 3, 4]);
        var request = new CreateWorldRequest
        {
            Params = new WorldParams { Seed = "Rimlink", ScenarioDef = "Crashlanded", StorytellerDef = "Phoebe", DifficultyDef = "Custom", DifficultyValues = values },
        };

        var created = await world.CreateAsync(request, Bearer(admin.SessionToken));
        var read = await world.GetAsync(new Empty(), Bearer(admin.SessionToken));

        Assert.True(created.StorytellerLocked);
        Assert.Equal(values, created.Params.DifficultyValues);
        Assert.Equal(values, read.Params.DifficultyValues);
    }
}
