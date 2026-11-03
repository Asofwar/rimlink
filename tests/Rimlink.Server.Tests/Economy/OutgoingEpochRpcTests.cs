using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Rimlink.Protocol.V1;
using Rimlink.Server.Storage;
using Rimlink.Server.Storage.Entities;
using Rimlink.Server.Tests.Support;
using Xunit;
using static Rimlink.Server.Tests.Support.GrpcTestExtensions;
using static Rimlink.Server.Tests.Support.LiveFixtures;
using static Rimlink.Server.Tests.Support.RaidFixtures;

namespace Rimlink.Server.Tests.Economy;

public sealed class OutgoingEpochRpcTests
{
    [Theory]
    [InlineData("launch")]
    [InlineData("land")]
    [InlineData("report")]
    [InlineData("live-propose")]
    [InlineData("live-report")]
    [InlineData("shop-ack")]
    [InlineData("shop-publish")]
    public async Task StaleTokensCannotCommitMapOperations(string operation)
    {
        using var fixture = LiveServer();
        var nova = await Connected(fixture, "nova"); var bob = await Connected(fixture, "bob");
        var n = RaidClientsOf(fixture, nova.Login.SessionToken); var b = RaidClientsOf(fixture, bob.Login.SessionToken);
        byte[] bytes = SaveFixtures.MakeSave(ServerFixture.WorldSeed, "Bob", tick: 10);
        await SaveFixtures.Upload(b.Saves, bob.Login.SessionToken, bytes, SaveFixtures.HeaderFor(bytes, ServerFixture.WorldSeed, "Bob", tick: 10));
        await MakeTargetRaidable(fixture, bob.PlayerId);
        Func<Task> send;
        long actor = bob.PlayerId;
        switch (operation)
        {
            case "launch":
                actor = nova.PlayerId;
                send = async () => await n.Raids.LaunchAsync(Launch(bob.PlayerId, timing: RaidTiming.TimingInstant), n.Auth);
                break;
            case "land":
            case "report":
                var raid = await n.Raids.LaunchAsync(Launch(bob.PlayerId, timing: RaidTiming.TimingInstant), n.Auth);
                await b.Raids.AnnounceAsync(new RaidTick { RaidId = raid.Id, GameTick = 100 }, b.Auth);
                if (operation == "report") await b.Raids.LandAsync(new RaidTick { RaidId = raid.Id, GameTick = 100 }, b.Auth);
                send = operation == "land"
                    ? async () => await b.Raids.LandAsync(new RaidTick { RaidId = raid.Id, GameTick = 200 }, b.Auth)
                    : async () => await b.Raids.ReportAsync(AllAlive(raid.Id, 0, 2), b.Auth);
                break;
            case "live-propose":
                actor = nova.PlayerId;
                send = async () => await nova.Live.ProposeAsync(Propose(bob.PlayerId, LiveKind.LiveVisit), nova.Auth);
                break;
            case "live-report":
                var live = await nova.Live.ProposeAsync(Propose(bob.PlayerId, LiveKind.LiveVisit), nova.Auth);
                send = async () => await b.Raids.ReportAsync(AllAlive(0, live.Id, 2), b.Auth);
                break;
            case "shop-ack":
                using (var seed = fixture.Services.CreateScope())
                {
                    var db = seed.ServiceProvider.GetRequiredService<RimlinkDbContext>();
                    db.PendingRemovals.Add(new PendingRemoval { Id = 1, HostPlayerId = bob.PlayerId, ThingId = 51, Count = 1 });
                    await db.SaveChangesAsync();
                }
                var ack = new RemovalAck { GameTick = 200 }; ack.Ids.Add(1);
                send = async () => await b.Shop.AckRemovalAsync(ack, b.Auth);
                break;
            default:
                send = async () => await b.Shop.PublishAsync(new ShopList(), b.Auth);
                break;
        }
        using (var scope = fixture.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<RimlinkDbContext>().Players.Where(p => p.Id == actor)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.SaveWriteEpoch, 1));
        var error = await Assert.ThrowsAsync<RpcException>(send);
        Assert.Equal(ErrorCode.SaveWriteEpochMismatch, error.ErrorDetail()?.Code);
        using var check = fixture.Services.CreateScope(); var after = check.ServiceProvider.GetRequiredService<RimlinkDbContext>();
        Assert.False(await after.Parcels.AnyAsync());
        Assert.False(await after.PendingRemovals.AnyAsync(p => p.AckedAt != null));
        if (operation == "launch") Assert.False(await after.Raids.AnyAsync());
        if (operation == "live-propose") Assert.False(await after.LiveSessions.AnyAsync());
    }
}
