using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Rimlink.Protocol.V1;
using Rimlink.Server.Storage;
using Rimlink.Server.Storage.Entities;
using static Rimlink.Server.Tests.Support.GrpcTestExtensions;

namespace Rimlink.Server.Tests.Support;

public sealed class InfluenceFixtures : IDisposable
{
    public ServerFixture Server { get; } = new ServerFixture().WithSetting("Auth:AllowPassword", "true")
        .WithSetting("Admin:ApiToken", AdminRestExtensions.ApiToken)
        .WithToken(0, "reader", "read-token-for-influence-0001", "read");
    public LoginReply Owner { get; private set; } = null!;
    public LoginReply Peer { get; private set; } = null!;
    public long HomeId { get; private set; }
    public long PeerHomeId { get; private set; }
    public long AllianceId { get; private set; }
    public global::Rimlink.Protocol.V1.Influence.InfluenceClient Client => new(Server.CreateChannel());

    public static async Task<InfluenceFixtures> Create()
    {
        var fixture = new InfluenceFixtures();
        fixture.Owner = await fixture.Server.RegisterAndLogin("owner");
        fixture.Peer = await fixture.Server.RegisterAndLogin("peer");
        await fixture.Db(async db =>
        {
            var group = new AllianceGroup { Name = "North", OwnerId = fixture.Owner.Player.PlayerId };
            db.Add(group); await db.SaveChangesAsync(); fixture.AllianceId = group.Id;
            db.AddRange(new AllianceMember { AllianceId = group.Id, PlayerId = fixture.Owner.Player.PlayerId }, new AllianceMember { AllianceId = group.Id, PlayerId = fixture.Peer.Player.PlayerId });
            var home = new Server.Storage.Entities.WorldObject { OwnerPlayerId = fixture.Owner.Player.PlayerId, Tile = 10, Name = "Home" };
            var peerHome = new Server.Storage.Entities.WorldObject { OwnerPlayerId = fixture.Peer.Player.PlayerId, Tile = 20, Name = "Peer" };
            db.AddRange(home, peerHome); await db.SaveChangesAsync(); fixture.HomeId = home.Id; fixture.PeerHomeId = peerHome.Id;
        });
        return fixture;
    }

    public async Task Db(Func<RimlinkDbContext, Task> action)
    {
        using var scope = Server.Services.CreateScope();
        await action(scope.ServiceProvider.GetRequiredService<RimlinkDbContext>());
    }

    public async Task<InfluenceSetting> Setting(bool peer = false) => await Client.GetSettingAsync(new InfluenceTarget { SettlementId = peer ? PeerHomeId : HomeId }, Bearer((peer ? Peer : Owner).SessionToken));
    public async Task<InfluenceReceipt> Send(InfluenceMutation request, bool peer = false) => await Client.MutateAsync(request, Bearer((peer ? Peer : Owner).SessionToken));
    public static InfluenceMutation Publication(InfluenceSetting setting, bool enabled, string? requestId = null) => new()
    {
        ClientRequestId = requestId ?? Guid.NewGuid().ToString("N"),
        Publication = new InfluencePublication { Context = setting.Context.Clone(), ExpectedVersion = setting.Version, Enabled = enabled },
    };

    public async Task<InfluenceSetting> ConfirmSurface(bool root = true, bool peer = false)
    {
        var setting = await Setting(peer);
        var receipt = await Send(new InfluenceMutation { ClientRequestId = Guid.NewGuid().ToString("N"), Surface = new InfluenceSurface { Context = setting.Context.Clone(), ExpectedVersion = setting.Version, Tile = setting.Tile, RootSurface = root } }, peer);
        Xunit.Assert.Null(receipt.Error);
        return await Setting(peer);
    }

    public async Task<InfluenceSnapshot> Snapshot() => await Client.GetAsync(new Empty(), Bearer(Owner.SessionToken));

    public Task SeedPublished(int count) => Db(async db =>
    {
        string world = (await db.World.SingleAsync()).InfluenceInstanceId;
        for (int i = 0; i < count; i++)
            db.Add(new Server.Storage.Entities.WorldObject
            {
                OwnerPlayerId = Owner.Player.PlayerId, Tile = 100 + i, Name = "Offline" + i,
                InfluenceEnabled = true, InfluenceRootSurface = true, InfluenceWorldId = world,
                InfluenceAllianceId = AllianceId, InfluenceTile = 100 + i,
            });
        await db.SaveChangesAsync();
    });

    public void Dispose() => Server.Dispose();
}
