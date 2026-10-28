using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using Rimlink.Protocol.V1;
using Rimlink.Server.Tests.Support;
using Xunit;
using static Rimlink.Server.Tests.Support.EconomyFixtures;
using static Rimlink.Server.Tests.Support.GrpcTestExtensions;
using static Rimlink.Server.Tests.Support.RaidFixtures;
using static Rimlink.Server.Tests.Support.SaveFixtures;

namespace Rimlink.Server.Tests.Visits;

public class VisitTests
{
    /// <summary>Streams another player's save through DownloadOf.</summary>
    public static async Task<byte[]> DownloadOf(RaidClients c, long playerId)
    {
        using var call = c.Saves.DownloadOf(new PlayerRef { PlayerId = playerId }, c.Auth);
        using var buffer = new MemoryStream();
        while (await call.ResponseStream.MoveNext(CancellationToken.None))
        {
            if (call.ResponseStream.Current.PartCase == SaveChunk.PartOneofCase.Data) call.ResponseStream.Current.Data.WriteTo(buffer);
        }
        return buffer.ToArray();
    }

    [Fact]
    public async Task BeginNeedsAHostSaveAndOpensATicket()
    {
        using var fixture = EconomyServer();
        var nova = await fixture.RegisterAndLogin("nova", playerName: "Nova");
        var bob = await fixture.RegisterAndLogin("bob", playerName: "Bob");
        var n = RaidClientsOf(fixture, nova.SessionToken);
        var events = new Protocol.V1.Events.EventsClient(fixture.CreateChannel());
        using var bobStream = events.Subscribe(new Empty(), Bearer(bob.SessionToken));

        var noSave = await Assert.ThrowsAsync<RpcException>(() => n.Visits.BeginAsync(new VisitRequest { TargetPlayerId = bob.Player.PlayerId, GameTick = 5 }, n.Auth).ResponseAsync);
        Assert.Equal(StatusCode.NotFound, noSave.StatusCode);

        byte[] save = MakeSave(ServerFixture.WorldSeed, "Bob", tick: 10);
        var b = RaidClientsOf(fixture, bob.SessionToken);
        await Upload(b.Saves, bob.SessionToken, save, HeaderFor(save, ServerFixture.WorldSeed, "Bob", tick: 10));
        var ticket = await n.Visits.BeginAsync(new VisitRequest { TargetPlayerId = bob.Player.PlayerId, GameTick = 5 }, n.Auth);

        Assert.True(ticket.VisitId > 0);
        Assert.Equal("Bob", ticket.Host.Name);
        Assert.Equal("Nova", await ReadEvent(bobStream, ServerEvent.BodyOneofCase.VisitStarted, e => e.VisitStarted.Visitor.Name));
    }

    [Fact]
    public async Task DownloadOfWorksOnlyDuringAVisit()
    {
        using var fixture = EconomyServer();
        var nova = await fixture.RegisterAndLogin("nova");
        var bob = await fixture.RegisterAndLogin("bob", playerName: "Bob");
        var n = RaidClientsOf(fixture, nova.SessionToken);
        var b = RaidClientsOf(fixture, bob.SessionToken);
        byte[] save = MakeSave(ServerFixture.WorldSeed, "Bob", tick: 10);
        await Upload(b.Saves, bob.SessionToken, save, HeaderFor(save, ServerFixture.WorldSeed, "Bob", tick: 10));

        var before = await Assert.ThrowsAsync<RpcException>(() => DownloadOf(n, bob.Player.PlayerId));
        Assert.Equal(StatusCode.PermissionDenied, before.StatusCode);

        var ticket = await n.Visits.BeginAsync(new VisitRequest { TargetPlayerId = bob.Player.PlayerId, GameTick = 5 }, n.Auth);
        Assert.Equal(save, await DownloadOf(n, bob.Player.PlayerId));

        await n.Visits.EndAsync(new VisitRef { VisitId = ticket.VisitId }, n.Auth);
        await n.Visits.EndAsync(new VisitRef { VisitId = ticket.VisitId }, n.Auth); // idempotent
        var after = await Assert.ThrowsAsync<RpcException>(() => DownloadOf(n, bob.Player.PlayerId));
        Assert.Equal(StatusCode.PermissionDenied, after.StatusCode);
    }

    [Fact]
    public async Task ClosedVisitsAreRefused()
    {
        using var fixture = EconomyServer();
        var nova = await fixture.RegisterAndLogin("nova");
        var bob = await fixture.RegisterAndLogin("bob", playerName: "Bob");
        var n = RaidClientsOf(fixture, nova.SessionToken);
        var b = RaidClientsOf(fixture, bob.SessionToken);
        byte[] save = MakeSave(ServerFixture.WorldSeed, "Bob", tick: 10);
        await Upload(b.Saves, bob.SessionToken, save, HeaderFor(save, ServerFixture.WorldSeed, "Bob", tick: 10));
        await b.Raids.SetVisitsClosedAsync(new VisitsClosedRequest { Closed = true }, b.Auth);

        var ex = await Assert.ThrowsAsync<RpcException>(() => n.Visits.BeginAsync(new VisitRequest { TargetPlayerId = bob.Player.PlayerId, GameTick = 5 }, n.Auth).ResponseAsync);

        Assert.Equal(ErrorCode.VisitsClosed, ex.ErrorDetail()!.Code);
        var self = await Assert.ThrowsAsync<RpcException>(() => n.Visits.BeginAsync(new VisitRequest { TargetPlayerId = nova.Player.PlayerId, GameTick = 5 }, n.Auth).ResponseAsync);
        Assert.Equal(ErrorCode.SelfTrade, self.ErrorDetail()!.Code);
    }
}
