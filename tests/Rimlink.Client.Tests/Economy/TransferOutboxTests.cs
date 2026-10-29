using Google.Protobuf;
using Grpc.Core;
using Rimlink.Client.Economy;
using Rimlink.Client.Net;
using Rimlink.Client.Tests.Support;
using Rimlink.Protocol.V1;
using Rimlink.Server.Tests.Support;
using Xunit;
using static Rimlink.Server.Tests.Support.EconomyFixtures;
using static Rimlink.Server.Tests.Support.GrpcTestExtensions;

namespace Rimlink.Client.Tests.Economy;

/// <summary>A transfer whose answer never came is replayed, never guessed: the server's idempotency keys make the replay safe.</summary>
public class TransferOutboxTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "rimlink-outbox-" + Guid.NewGuid().ToString("N"));

    private TransferOutbox Outbox() => new(Path.Combine(dir, "outbox.json"), new ConsoleLog());

    public void Dispose()
    {
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public async Task ReplayingASendEntryTwiceCreatesOneParcel()
    {
        using var fixture = EconomyServer();
        await using var nova = await ClientFixtures.Connected(fixture, "nova");
        var bob = await fixture.RegisterAndLogin("bob");
        var outbox = Outbox();
        outbox.Add(Send(bob.Player.PlayerId, Items(("Steel", 5))));
        var economy = new EconomyClient(nova);

        var first = await outbox.Replay(economy, CancellationToken.None);
        var second = await Outbox().Replay(economy, CancellationToken.None); // a fresh instance reads the same file

        Assert.Equal(1, first.Completed);
        Assert.Equal(0, second.Completed);
        Assert.Empty(Outbox().Entries);
        var b = Clients(fixture, bob.SessionToken);
        Assert.Single((await b.Mailbox.ListAsync(new Empty(), b.Auth)).Parcels);
    }

    [Fact]
    public async Task AnEntryThatGotNoAnswerIsReplayedWithTheSameKey()
    {
        using var fixture = EconomyServer();
        await using var nova = await ClientFixtures.Connected(fixture, "nova");
        var bob = await fixture.RegisterAndLogin("bob");
        var request = Send(bob.Player.PlayerId, Items(("Steel", 5)));
        var n = Clients(fixture, nova.Login!.SessionToken);
        await n.Transfer.SendAsync(request, n.Auth); // the server took it, the answer was lost
        var outbox = Outbox();
        outbox.Add(request);

        await outbox.Replay(new EconomyClient(nova), CancellationToken.None);

        Assert.Empty(outbox.Entries);
        var b = Clients(fixture, bob.SessionToken);
        Assert.Single((await b.Mailbox.ListAsync(new Empty(), b.Auth)).Parcels);
    }

    [Fact]
    public async Task DeliverEntryIsRemovedAfterReplay()
    {
        using var fixture = EconomyServer();
        await using var bob = await ClientFixtures.Connected(fixture, "bob");
        var nova = await fixture.RegisterAndLogin("nova");
        var n = Clients(fixture, nova.SessionToken);
        var parcel = await n.Transfer.SendAsync(Send(bob.Login!.Player.PlayerId, Items(("Steel", 5))), n.Auth);
        var outbox = Outbox();
        outbox.Add(new DeliverRequest { ParcelId = parcel.Id, GameTick = 100 });

        var report = await outbox.Replay(new EconomyClient(bob), CancellationToken.None);

        Assert.Equal(1, report.Completed);
        Assert.Empty(outbox.Entries);
        var listed = Assert.Single((await new EconomyClient(bob).Pending(CancellationToken.None)).Parcels);
        Assert.True(listed.Redeliver); // delivered at tick 100 with no save that holds it yet: the server offers it again
    }

    [Fact]
    public async Task ARefusedSendStaysWithItsReasonUntilRemoved()
    {
        using var fixture = EconomyServer();
        await using var nova = await ClientFixtures.Connected(fixture, "nova");
        var outbox = Outbox();
        var entry = outbox.Add(Send(nova.Login!.Player.PlayerId, Items(("Steel", 5)))); // to self: refused for good

        var report = await outbox.Replay(new EconomyClient(nova), CancellationToken.None);

        Assert.Equal(1, report.Refused);
        var kept = Assert.Single(Outbox().Entries);
        Assert.Equal(entry.Id, kept.Id);
        Assert.NotNull(kept.Refusal);
        Assert.Equal(5, Assert.IsType<TransferRequest>(TransferOutbox.Decode(kept)).Items.Items[0].Count);
        outbox.Remove(entry.Id);
        Assert.Empty(Outbox().Entries);
    }

    [Fact]
    public async Task ARefusedSilverOnlySendIsDropped()
    {
        using var fixture = EconomyServer();
        await using var nova = await ClientFixtures.Connected(fixture, "nova");
        var outbox = Outbox();
        var send = Send(nova.Login!.Player.PlayerId, new ItemList()); // to self: refused for good
        send.Silver = 50; // wallet silver never left the colony as things: nothing comes back by pod
        outbox.Add(send);

        var report = await outbox.Replay(new EconomyClient(nova), CancellationToken.None);

        Assert.Equal(1, report.Refused);
        Assert.Empty(Outbox().Entries);
    }

    [Fact]
    public void IndeterminateStatusIsNotDefinitive()
    {
        Assert.False(TransferOutbox.IsDefinitive(new RpcException(new Status(StatusCode.Unavailable, "down"))));
        Assert.False(TransferOutbox.IsDefinitive(new RpcException(new Status(StatusCode.DeadlineExceeded, "slow"))));
        Assert.False(TransferOutbox.IsDefinitive(new RpcException(new Status(StatusCode.Internal, "broken"))));
        Assert.False(TransferOutbox.IsDefinitive(new RpcException(new Status(StatusCode.Unauthenticated, "token"))));
        Assert.True(TransferOutbox.IsDefinitive(new RpcException(new Status(StatusCode.FailedPrecondition, "no"))));
        Assert.True(TransferOutbox.IsDefinitive(new RpcException(new Status(StatusCode.InvalidArgument, "no"))));
        Assert.True(TransferOutbox.IsDefinitive(new RpcException(new Status(StatusCode.NotFound, "no"))));
        Assert.False(TransferOutbox.IsDefinitive(new RpcException(new Status(StatusCode.ResourceExhausted, "slow down"))));
        var refused = new Metadata { { "rimlink-error-bin", new ErrorDetail { Code = ErrorCode.RecipientUnknown, Message = "gone" }.ToByteArray() } };
        Assert.True(TransferOutbox.IsDefinitive(new RpcException(new Status(StatusCode.FailedPrecondition, "x"), refused)));
        var frozen = new Metadata { { "rimlink-error-bin", new ErrorDetail { Code = ErrorCode.EconomyFrozen, Message = "frozen" }.ToByteArray() } };
        Assert.False(TransferOutbox.IsDefinitive(new RpcException(new Status(StatusCode.FailedPrecondition, "x"), frozen)));
    }

    [Fact]
    public void EntriesSurviveAReload()
    {
        var outbox = Outbox();
        outbox.Add(new DepositRequest { ClientRequestId = "d1", Silver = 10, GameTick = 7 });
        outbox.Add(new PostRequest { ClientRequestId = "p1", Price = 3, GameTick = 7, Items = Items(("Steel", 1)) });

        var entries = Outbox().Entries;

        Assert.Equal(2, entries.Count);
        Assert.Equal(10, Assert.IsType<DepositRequest>(TransferOutbox.Decode(entries[0])).Silver);
        Assert.Equal(3, Assert.IsType<PostRequest>(TransferOutbox.Decode(entries[1])).Price);
    }
}
