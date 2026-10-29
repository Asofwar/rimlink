using Grpc.Core;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.World;
using Rimlink.Server.Hosting;
using Rimlink.Server.Tests.Support;
using Xunit;
using static Rimlink.Server.Tests.Support.GrpcTestExtensions;
using static Rimlink.Server.Tests.Support.HygieneFixtures;
using static Rimlink.Server.Tests.Support.SaveFixtures;

namespace Rimlink.Server.Tests.World;

/// <summary>Settlements carry the owner's wealth tier, colony age and last day seen, and update live on upload (spec §4).</summary>
public class WorldObjectLiveTests
{
    [Theory]
    [InlineData(0, false, 0)]
    [InlineData(0, true, 0)]
    [InlineData(49999, true, 1)]
    [InlineData(50000, true, 2)]
    [InlineData(149999, true, 2)]
    [InlineData(150000, true, 3)]
    [InlineData(400000, true, 4)]
    [InlineData(999999, true, 4)]
    [InlineData(1000000, true, 5)]
    [InlineData(70000, false, 0)]
    public void TierBoundaries(long wealth, bool known, int tier) =>
        Assert.Equal(tier, WealthTiers.Of(wealth, known, HygieneOptions.DefaultWealthTiers));

    private static async Task<Protocol.V1.WorldObject> ObjectOf(ServerFixture fixture, string token, long id) =>
        (await new Protocol.V1.World.WorldClient(fixture.CreateChannel()).ListObjectsAsync(new Empty(), Bearer(token))).Objects.Single(o => o.Id == id);

    [Fact]
    public async Task ListObjectsCarriesTierAgeAndLastSeen()
    {
        using var fixture = HygieneServer();
        var nova = await Settled(fixture, "nova", tile: 100, wealth: 200000, tick: 45 * GameDay);
        var mira = await fixture.RegisterAndLogin("mira", playerName: "Mira");

        var o = await ObjectOf(fixture, mira.SessionToken, nova.Settlement.Id);

        Assert.Equal(3, o.WealthTier);
        Assert.True(o.HasColonyGameDays);
        Assert.Equal(45, o.ColonyGameDays);
        Assert.Equal(DateTime.UtcNow.Date, o.OwnerLastSeen.ToDateTime());
    }

    [Fact]
    public async Task NoSaveMeansNoAgeAndNoTier()
    {
        using var fixture = HygieneServer();
        var nova = await fixture.RegisterAndLogin("nova", playerName: "Nova");
        var settled = await new Protocol.V1.World.WorldClient(fixture.CreateChannel()).SettleAsync(new SettleRequest { Tile = 100, Name = "Nova Home" }, Bearer(nova.SessionToken));

        Assert.False(settled.HasColonyGameDays);
        Assert.Equal(0, settled.WealthTier);
        var listed = await ObjectOf(fixture, nova.SessionToken, settled.Id);
        Assert.False(listed.HasColonyGameDays);
        Assert.Equal(0, listed.WealthTier);
    }

    private static AsyncServerStreamingCall<ServerEvent> Stream(ServerFixture fixture, string token) =>
        new Protocol.V1.Events.EventsClient(fixture.CreateChannel()).Subscribe(new Empty(), Bearer(token));

    /// <summary>Reads until a PlayerJoined for <paramref name="marker"/>; returns the events seen before it.</summary>
    private static async Task<List<ServerEvent>> Until(AsyncServerStreamingCall<ServerEvent> stream, string marker)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var seen = new List<ServerEvent>();
        while (await stream.ResponseStream.MoveNext(timeout.Token))
        {
            var ev = stream.ResponseStream.Current;
            if (ev.BodyCase == ServerEvent.BodyOneofCase.PlayerJoined && ev.PlayerJoined.Player.Name == marker) return seen;
            seen.Add(ev);
        }
        throw new InvalidOperationException("stream ended");
    }

    private static async Task Upload(ServerFixture fixture, HygieneFixtures.Colony colony, long tick, long wealth)
    {
        byte[] save = MakeSave(ServerFixture.WorldSeed, "Nova", tick);
        var header = HeaderFor(save, ServerFixture.WorldSeed, "Nova", tick);
        header.WealthTotal = wealth;
        header.WealthItems = wealth / 2;
        await SaveFixtures.Upload(new Protocol.V1.Saves.SavesClient(fixture.CreateChannel()), colony.Token, save, header);
    }

    [Fact]
    public async Task UploadThatChangesTheTierBroadcastsAnUpdate()
    {
        using var fixture = HygieneServer();
        var nova = await Settled(fixture, "nova", tile: 100, wealth: 50000, tick: 1000);
        var mira = await fixture.RegisterAndLogin("mira", playerName: "Mira");
        using var miraStream = Stream(fixture, mira.SessionToken);
        using var novaStream = Stream(fixture, nova.Token);
        await fixture.RegisterAndLogin("zed", playerName: "Zed");
        await Until(miraStream, "Zed");
        await Until(novaStream, "Zed");

        await Upload(fixture, nova, tick: 2000, wealth: 600000);
        await fixture.RegisterAndLogin("yan", playerName: "Yan");

        var update = Assert.Single(await Until(miraStream, "Yan"), e => e.BodyCase == ServerEvent.BodyOneofCase.WorldObjectUpdated).WorldObjectUpdated.Object;
        Assert.Equal((nova.Settlement.Id, 4), (update.Id, update.WealthTier));
        Assert.DoesNotContain(await Until(novaStream, "Yan"), e => e.BodyCase == ServerEvent.BodyOneofCase.WorldObjectUpdated);
    }

    [Fact]
    public async Task UploadWithSameTierAndDaySendsNoUpdate()
    {
        using var fixture = HygieneServer();
        var nova = await Settled(fixture, "nova", tile: 100, wealth: 50000, tick: 1000);
        var mira = await fixture.RegisterAndLogin("mira", playerName: "Mira");
        using var miraStream = Stream(fixture, mira.SessionToken);
        await fixture.RegisterAndLogin("zed", playerName: "Zed");
        await Until(miraStream, "Zed");

        await Upload(fixture, nova, tick: 2000, wealth: 60000);
        await fixture.RegisterAndLogin("yan", playerName: "Yan");

        Assert.DoesNotContain(await Until(miraStream, "Yan"), e => e.BodyCase == ServerEvent.BodyOneofCase.WorldObjectUpdated);
    }

    [Fact]
    public async Task ADayOlderColonyBroadcastsAnUpdate()
    {
        using var fixture = HygieneServer();
        var nova = await Settled(fixture, "nova", tile: 100, wealth: 50000, tick: 1000);
        var mira = await fixture.RegisterAndLogin("mira", playerName: "Mira");
        using var miraStream = Stream(fixture, mira.SessionToken);
        await fixture.RegisterAndLogin("zed", playerName: "Zed");
        await Until(miraStream, "Zed");

        await Upload(fixture, nova, tick: GameDay + 5, wealth: 50000);
        await fixture.RegisterAndLogin("yan", playerName: "Yan");

        var update = Assert.Single(await Until(miraStream, "Yan"), e => e.BodyCase == ServerEvent.BodyOneofCase.WorldObjectUpdated).WorldObjectUpdated.Object;
        Assert.Equal(1, update.ColonyGameDays);
    }

    [Fact]
    public void ExactWealthIsNeverSent()
    {
        var fields = Protocol.V1.WorldObject.Descriptor.Fields.InDeclarationOrder().Select(f => f.Name).ToList();

        Assert.Equal(["id", "type", "owner", "tile", "name", "payload", "wealth_tier", "owner_last_seen", "colony_game_days"], fields);
    }

    [Fact]
    public async Task WorldInfoCarriesTheTierBounds()
    {
        using var fixture = HygieneServer().WithSetting("Hygiene:WealthTiers:0", "10").WithSetting("Hygiene:WealthTiers:1", "20")
            .WithSetting("Hygiene:WealthTiers:2", "30").WithSetting("Hygiene:WealthTiers:3", "40");
        var nova = await fixture.RegisterAndLogin("nova", playerName: "Nova");

        var info = await new Protocol.V1.World.WorldClient(fixture.CreateChannel()).GetAsync(new Empty(), Bearer(nova.SessionToken));

        Assert.Equal([10L, 20L, 30L, 40L], info.WealthTiers);
    }
}
