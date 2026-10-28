using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Events;
using Rimlink.Server.Application.Sessions;
using Rimlink.Server.Storage.Entities;
using Rimlink.Server.Tests.Support;
using Xunit;
using static Rimlink.Server.Tests.Support.GrpcTestExtensions;

namespace Rimlink.Server.Tests.Events;

public class EventStreamTests
{
    private static ServerEvent Notice(string text) => new() { Notice = new Notice { Text = text } };

    [Fact]
    public async Task BroadcastReachesAllSubscribers()
    {
        using var fixture = new ServerFixture().WithSetting("Auth:AllowPassword", "true");
        var a = await fixture.RegisterAndLogin("alice");
        var b = await fixture.RegisterAndLogin("bob");
        var events = new Protocol.V1.Events.EventsClient(fixture.CreateChannel());
        using var streamA = events.Subscribe(new Empty(), Bearer(a.SessionToken));
        using var streamB = events.Subscribe(new Empty(), Bearer(b.SessionToken));
        var bus = fixture.Services.GetRequiredService<EventBus>();

        bus.Broadcast(Notice("one"));
        bus.Broadcast(Notice("two"));

        var gotA = await ReadNotices(streamA, 2);
        var gotB = await ReadNotices(streamB, 2);
        Assert.Equal(["one", "two"], gotA.Select(e => e.Notice.Text));
        Assert.Equal(["one", "two"], gotB.Select(e => e.Notice.Text));
        Assert.True(gotA[1].Sequence > gotA[0].Sequence);
        Assert.Equal(gotA.Select(e => e.Sequence), gotB.Select(e => e.Sequence));
    }

    [Fact]
    public async Task SendToReachesOnlyTheTarget()
    {
        using var fixture = new ServerFixture().WithSetting("Auth:AllowPassword", "true");
        var a = await fixture.RegisterAndLogin("alice");
        var b = await fixture.RegisterAndLogin("bob");
        var events = new Protocol.V1.Events.EventsClient(fixture.CreateChannel());
        using var streamA = events.Subscribe(new Empty(), Bearer(a.SessionToken));
        using var streamB = events.Subscribe(new Empty(), Bearer(b.SessionToken));
        var bus = fixture.Services.GetRequiredService<EventBus>();

        bus.SendTo(b.Player.PlayerId, Notice("private"));
        bus.Broadcast(Notice("public"));

        Assert.Equal(["public"], (await ReadNotices(streamA, 1)).Select(e => e.Notice.Text));
        Assert.Equal(["private", "public"], (await ReadNotices(streamB, 2)).Select(e => e.Notice.Text));
    }

    [Fact]
    public async Task SubscribeWithoutSessionIsUnauthenticated()
    {
        using var fixture = new ServerFixture();
        var events = new Protocol.V1.Events.EventsClient(fixture.CreateChannel());

        using var stream = events.Subscribe(new Empty());
        var ex = await Assert.ThrowsAsync<RpcException>(() => stream.ResponseStream.MoveNext(CancellationToken.None));

        Assert.Equal(StatusCode.Unauthenticated, ex.StatusCode);
    }

    [Fact]
    public async Task EndingTheSessionEndsTheStreamAsUnavailable()
    {
        using var fixture = new ServerFixture().WithSetting("Auth:AllowPassword", "true");
        var a = await fixture.RegisterAndLogin("alice");
        var events = new Protocol.V1.Events.EventsClient(fixture.CreateChannel());
        using var stream = events.Subscribe(new Empty(), Bearer(a.SessionToken));
        fixture.Services.GetRequiredService<EventBus>().Broadcast(Notice("hello"));
        Assert.Single(await ReadNotices(stream, 1)); // subscription is live

        fixture.Services.GetRequiredService<SessionRegistry>().End(a.SessionToken);

        var ex = await Assert.ThrowsAsync<RpcException>(() => stream.ResponseStream.MoveNext(CancellationToken.None));
        Assert.Equal(StatusCode.Unavailable, ex.StatusCode);
    }

    [Fact]
    public void SlowConsumerIsDisconnectedOthersKeepReceiving()
    {
        var sessions = new SessionRegistry();
        var bus = new EventBus(sessions, NullLogger<EventBus>.Instance);
        var slow = sessions.Start(new Account { Id = 1 }, new Player { Id = 1, Name = "slow" }, "ip", out _);
        var fast = sessions.Start(new Account { Id = 2 }, new Player { Id = 2, Name = "fast" }, "ip", out _);
        var fastReads = fast.Events.ReadAll(CancellationToken.None).GetAsyncEnumerator();

        for (int i = 0; i < EventQueue.Capacity + 100; i++)
        {
            bus.Broadcast(Notice(i.ToString()));
            Assert.True(fastReads.MoveNextAsync().AsTask().GetAwaiter().GetResult());
        }

        Assert.False(sessions.TryGet(slow.Token, out _));
        Assert.True(slow.Cts.IsCancellationRequested);
        Assert.True(sessions.TryGet(fast.Token, out _));
        Assert.Equal((EventQueue.Capacity + 99).ToString(), fastReads.Current.Notice.Text);
    }

    private static async Task<List<ServerEvent>> ReadNotices(AsyncServerStreamingCall<ServerEvent> call, int count)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var result = new List<ServerEvent>();
        while (result.Count < count && await call.ResponseStream.MoveNext(cts.Token))
        {
            if (call.ResponseStream.Current.BodyCase == ServerEvent.BodyOneofCase.Notice) result.Add(call.ResponseStream.Current);
        }
        return result;
    }
}
