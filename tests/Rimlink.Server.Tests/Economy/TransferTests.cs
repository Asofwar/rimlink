using Google.Protobuf;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Rimlink.Protocol.V1;
using Rimlink.Server.Storage;
using Rimlink.Server.Tests.Support;
using Xunit;
using static Rimlink.Server.Tests.Support.EconomyFixtures;
using static Rimlink.Server.Tests.Support.GrpcTestExtensions;

namespace Rimlink.Server.Tests.Economy;

public class TransferTests
{
    [Fact]
    public async Task SendCreatesAParcelTheRecipientSeesAndAnEvent()
    {
        using var fixture = EconomyServer();
        var nova = await fixture.RegisterAndLogin("nova", playerName: "Nova");
        var bob = await fixture.RegisterAndLogin("bob", playerName: "Bob");
        var events = new Protocol.V1.Events.EventsClient(fixture.CreateChannel());
        using var bobStream = events.Subscribe(new Empty(), Bearer(bob.SessionToken));
        var c = Clients(fixture, nova.SessionToken);

        var parcel = await c.Transfer.SendAsync(Send(bob.Player.PlayerId, Items(("Steel", 10)), tick: 500), c.Auth);

        Assert.True(parcel.Id > 0);
        var bobBox = await Clients(fixture, bob.SessionToken).Mailbox.ListAsync(new Empty(), Bearer(bob.SessionToken));
        var got = Assert.Single(bobBox.Parcels);
        Assert.Equal(ParcelKind.ParcelGift, got.Kind);
        Assert.Equal("Nova", got.From.Name);
        Assert.Equal(10, got.Items.Items[0].Count);
        Assert.Equal(parcel.Id, await ReadEvent(bobStream, ServerEvent.BodyOneofCase.ParcelArrived, e => e.ParcelArrived.Parcel.Id));
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RimlinkDbContext>();
        Assert.Equal(500, (await db.Players.SingleAsync(p => p.Id == nova.Player.PlayerId)).LastOutgoingTick);
        Assert.Single(db.Transfers.Where(t => t.FromPlayerId == nova.Player.PlayerId && t.Kind == "send"));
    }

    [Fact]
    public async Task SendingToYourselfIsSelfTrade()
    {
        using var fixture = EconomyServer();
        var nova = await fixture.RegisterAndLogin("nova");
        var c = Clients(fixture, nova.SessionToken);

        var ex = await Assert.ThrowsAsync<RpcException>(() => c.Transfer.SendAsync(Send(nova.Player.PlayerId, Items(("Steel", 1))), c.Auth).ResponseAsync);

        Assert.Equal(ErrorCode.SelfTrade, ex.ErrorDetail()?.Code);
    }

    [Fact]
    public async Task SendingToAnUnknownPlayerIsRecipientUnknown()
    {
        using var fixture = EconomyServer();
        var nova = await fixture.RegisterAndLogin("nova");
        var c = Clients(fixture, nova.SessionToken);

        var ex = await Assert.ThrowsAsync<RpcException>(() => c.Transfer.SendAsync(Send(9999, Items(("Steel", 1))), c.Auth).ResponseAsync);

        Assert.Equal(ErrorCode.RecipientUnknown, ex.ErrorDetail()?.Code);
    }

    [Fact]
    public async Task SendingMoreSilverThanTheWalletHoldsIsWalletInsufficient()
    {
        using var fixture = EconomyServer();
        var nova = await fixture.RegisterAndLogin("nova");
        var bob = await fixture.RegisterAndLogin("bob");
        var c = Clients(fixture, nova.SessionToken);

        var ex = await Assert.ThrowsAsync<RpcException>(() => c.Transfer.SendAsync(Send(bob.Player.PlayerId, silver: 1), c.Auth).ResponseAsync);

        Assert.Equal(ErrorCode.WalletInsufficient, ex.ErrorDetail()?.Code);
    }

    [Fact]
    public async Task SameClientRequestIdReturnsTheSameParcel()
    {
        using var fixture = EconomyServer();
        var nova = await fixture.RegisterAndLogin("nova");
        var bob = await fixture.RegisterAndLogin("bob");
        var c = Clients(fixture, nova.SessionToken);

        var first = await c.Transfer.SendAsync(Send(bob.Player.PlayerId, Items(("Steel", 5)), requestId: "req-1"), c.Auth);
        var second = await c.Transfer.SendAsync(Send(bob.Player.PlayerId, Items(("Steel", 5)), requestId: "req-1"), c.Auth);

        Assert.Equal(first.Id, second.Id);
        Assert.Single((await Clients(fixture, bob.SessionToken).Mailbox.ListAsync(new Empty(), Bearer(bob.SessionToken))).Parcels);
    }

    [Theory]
    [InlineData(201, 0)]
    [InlineData(0, 11)]
    public async Task OversizedParcelIsParcelTooBig(int items, int pawns)
    {
        using var fixture = EconomyServer();
        var nova = await fixture.RegisterAndLogin("nova");
        var bob = await fixture.RegisterAndLogin("bob");
        var c = Clients(fixture, nova.SessionToken);
        var request = Send(bob.Player.PlayerId);
        for (int i = 0; i < items; i++) request.Items.Items.Add(Item("Steel", 1));
        for (int i = 0; i < pawns; i++) request.Pawns.Pawns.Add(Pawn("Husky", $"Dog{i}"));

        var ex = await Assert.ThrowsAsync<RpcException>(() => c.Transfer.SendAsync(request, c.Auth).ResponseAsync);

        Assert.Equal(ErrorCode.ParcelTooBig, ex.ErrorDetail()?.Code);
    }

    [Fact]
    public async Task ParcelOverMaxBytesIsParcelTooBig()
    {
        using var fixture = EconomyServer();
        var nova = await fixture.RegisterAndLogin("nova");
        var bob = await fixture.RegisterAndLogin("bob");
        var c = Clients(fixture, nova.SessionToken);
        var request = Send(bob.Player.PlayerId);
        request.Items.Items.Add(Item("Steel", 1, blobBytes: 5 * 1024 * 1024));

        var ex = await Assert.ThrowsAsync<RpcException>(() => c.Transfer.SendAsync(request, c.Auth).ResponseAsync);

        // Over the gRPC message limit the call fails before our validator; either way the client learns it is too big.
        Assert.True(ex.StatusCode == StatusCode.ResourceExhausted || ex.ErrorDetail()?.Code == ErrorCode.ParcelTooBig, ex.Status.ToString());
    }

    [Fact]
    public async Task ParcelJustOverTheConfiguredByteLimitIsParcelTooBig()
    {
        using var fixture = EconomyServer().WithSetting("Economy:MaxParcelBytes", "1000");
        var nova = await fixture.RegisterAndLogin("nova");
        var bob = await fixture.RegisterAndLogin("bob");
        var c = Clients(fixture, nova.SessionToken);
        var request = Send(bob.Player.PlayerId);
        request.Items.Items.Add(Item("Steel", 1, blobBytes: 1001));

        var ex = await Assert.ThrowsAsync<RpcException>(() => c.Transfer.SendAsync(request, c.Auth).ResponseAsync);

        Assert.Equal(ErrorCode.ParcelTooBig, ex.ErrorDetail()?.Code);
    }

    [Fact]
    public async Task RecipientWithFiftyPendingParcelsRefusesMore()
    {
        using var fixture = EconomyServer().WithSetting("Economy:MaxPendingParcels", "3");
        var nova = await fixture.RegisterAndLogin("nova");
        var bob = await fixture.RegisterAndLogin("bob");
        var c = Clients(fixture, nova.SessionToken);
        for (int i = 0; i < 3; i++) await c.Transfer.SendAsync(Send(bob.Player.PlayerId, Items(("Steel", 1))), c.Auth);

        var ex = await Assert.ThrowsAsync<RpcException>(() => c.Transfer.SendAsync(Send(bob.Player.PlayerId, Items(("Steel", 1))), c.Auth).ResponseAsync);

        Assert.Equal(StatusCode.FailedPrecondition, ex.StatusCode);
        Assert.Contains("mailbox is full", ex.ErrorDetail()?.Message);
    }

    [Fact]
    public async Task SendIsRateLimitedPerPlayer()
    {
        using var fixture = EconomyServer().WithRateLimits();
        var nova = await fixture.RegisterAndLogin("nova");
        var bob = await fixture.RegisterAndLogin("bob");
        var c = Clients(fixture, nova.SessionToken);

        await c.Transfer.SendAsync(Send(bob.Player.PlayerId, Items(("Steel", 1))), c.Auth);
        var ex = await Assert.ThrowsAsync<RpcException>(() => c.Transfer.SendAsync(Send(bob.Player.PlayerId, Items(("Steel", 1))), c.Auth).ResponseAsync);

        Assert.Equal(StatusCode.ResourceExhausted, ex.StatusCode);
    }
}
