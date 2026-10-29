using Rimlink.Protocol.V1;
using Rimlink.Server.Tests.Support;
using Xunit;
using static Rimlink.Server.Tests.Support.EconomyFixtures;
using static Rimlink.Server.Tests.Support.GrpcTestExtensions;

namespace Rimlink.Server.Tests.Social;

public class FlagsTests
{
    [Fact]
    public async Task AcceptGiftsAndTradesClosedRoundTrip()
    {
        using var fixture = EconomyServer();
        var nova = await fixture.RegisterAndLogin("nova", playerName: "Nova");
        var auth = Bearer(nova.SessionToken);
        var events = new Protocol.V1.Events.EventsClient(fixture.CreateChannel());
        using var stream = events.Subscribe(new Empty(), auth);
        var social = SocialFixtures.Social(fixture, nova.SessionToken).Social;
        var raids = new Protocol.V1.Raids.RaidsClient(fixture.CreateChannel());

        var gifts = await social.SetAcceptGiftsAsync(new FlagRequest { On = true }, auth);
        var changed = await ReadEvent(stream, ServerEvent.BodyOneofCase.PlayerFlagsChanged, e => e.PlayerFlagsChanged.Flags);
        var trades = await social.SetTradesClosedAsync(new FlagRequest { On = true }, auth);
        var peaceful = await raids.SetPeacefulAsync(new PeacefulRequest { Peaceful = true }, auth);
        var read = await raids.FlagsAsync(new Empty(), auth);

        Assert.True(gifts.AcceptGifts);
        Assert.True(changed.AcceptGifts);
        Assert.True(trades.AcceptGifts && trades.TradesClosed);
        Assert.True(peaceful.AcceptGifts && peaceful.TradesClosed && peaceful.Peaceful);
        Assert.True(read.AcceptGifts && read.TradesClosed);

        var off = await social.SetAcceptGiftsAsync(new FlagRequest { On = false }, auth);
        Assert.False(off.AcceptGifts);
        Assert.True(off.TradesClosed);
    }
}
