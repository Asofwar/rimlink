using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Admin;
using Rimlink.Server.Application.Sessions;
using Rimlink.Server.Application.Social;
using Rimlink.Server.Application.World;
using Rimlink.Server.Storage.Entities;
using Rimlink.Server.Tests.Support;
using Xunit;

namespace Rimlink.Server.Tests.Influence;

public sealed class InfluenceLifecycleTests
{
    [Fact]
    public async Task LeaveRevokesBothActiveAndArchivedConsentAndRejoinCannotRestoreIt()
    {
        using var test = await InfluenceFixtures.Create();
        Assert.Null((await test.Send(InfluenceFixtures.Publication(await test.ConfirmSurface(peer: true), true), peer: true)).Error);
        await test.Db(async db =>
        {
            var home = await db.WorldObjects.SingleAsync(o => o.Id == test.PeerHomeId);
            db.Add(new ArchivedColony { PlayerId = test.Peer.Player.PlayerId, Tile = 777, InfluenceSettlementId = 777, InfluenceEnabled = true, InfluenceRootSurface = true, InfluenceWorldId = home.InfluenceWorldId, InfluenceAllianceId = test.AllianceId, InfluenceVersion = 5 });
            await db.SaveChangesAsync();
        });
        using (var scope = test.Server.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<AllianceService>().Mutate(test.Peer.Player.PlayerId, new CommunityRequest { Action = CommunityAction.AllianceLeave, AllianceId = test.AllianceId, ClientRequestId = Guid.NewGuid().ToString("N") });
        Assert.Empty((await test.Snapshot()).Sources);
        await test.Db(async db =>
        {
            Assert.False((await db.ArchivedColonies.SingleAsync()).InfluenceEnabled);
            Assert.Null((await db.ArchivedColonies.SingleAsync()).InfluenceRootSurface);
            db.Add(new AllianceMember { PlayerId = test.Peer.Player.PlayerId, AllianceId = test.AllianceId }); await db.SaveChangesAsync();
        });
        Assert.False((await test.Setting(peer: true)).Enabled);
        Assert.False((await test.Setting(peer: true)).HasRootSurface);
    }

    [Fact]
    public async Task DisbandRemovesSourcesAndTheirPublicationConsent()
    {
        using var test = await InfluenceFixtures.Create();
        Assert.Null((await test.Send(InfluenceFixtures.Publication(await test.ConfirmSurface(), true))).Error);
        using (var scope = test.Server.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<AllianceService>().Mutate(test.Owner.Player.PlayerId, new CommunityRequest { Action = CommunityAction.AllianceDisband, AllianceId = test.AllianceId, ClientRequestId = Guid.NewGuid().ToString("N") });
        Assert.Empty((await test.Snapshot()).Sources);
        Assert.False((await test.Setting()).Enabled);
    }

    [Fact]
    public async Task ArchiveRestorePreservesConsentButUsesANewIdAndVersion()
    {
        using var test = await InfluenceFixtures.Create();
        Assert.Null((await test.Send(InfluenceFixtures.Publication(await test.ConfirmSurface(peer: true), true), peer: true)).Error);
        var before = await test.Setting(peer: true);
        test.Server.Services.GetRequiredService<SessionRegistry>().End(test.Peer.SessionToken);
        long id;
        using (var scope = test.Server.Services.CreateScope())
            id = Assert.IsType<ArchiveOutcome.Archived>(await scope.ServiceProvider.GetRequiredService<ColonyArchive>().Archive(test.Peer.Player.PlayerId, "admin", Actor.Api)).ArchiveId;
        Assert.Empty((await test.Snapshot()).Sources);
        using (var scope = test.Server.Services.CreateScope())
        {
            var restored = Assert.IsType<RestoreOutcome.Restored>(await scope.ServiceProvider.GetRequiredService<ColonyArchive>().Restore(id, "api"));
            Assert.NotEqual(before.Context.SettlementId, restored.Settlement.Id);
        }
        var current = Assert.Single((await test.Snapshot()).Sources);
        Assert.NotEqual(test.PeerHomeId, current.Id);
        await test.Db(async db => Assert.True((await db.WorldObjects.SingleAsync(o => o.Id == current.Id)).InfluenceVersion > before.Version));
    }

    [Fact]
    public async Task ResetClearsActiveAndArchivedProofAlongWithSaveGeneration()
    {
        using var test = await InfluenceFixtures.Create();
        Assert.Null((await test.Send(InfluenceFixtures.Publication(await test.ConfirmSurface(), true))).Error);
        using (var scope = test.Server.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<AdminOperations>().ResetPlayer(Actor.Api, test.Owner.Player.PlayerId);
        await test.Db(async db =>
        {
            Assert.False(await db.WorldObjects.AnyAsync(o => o.OwnerPlayerId == test.Owner.Player.PlayerId && o.InfluenceEnabled));
            Assert.Equal(1, (await db.Players.SingleAsync(p => p.Id == test.Owner.Player.PlayerId)).SaveGeneration);
        });
    }
}
