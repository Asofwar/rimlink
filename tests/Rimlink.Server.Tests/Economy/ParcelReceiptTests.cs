using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Economy;
using Rimlink.Server.Application.Saves;
using Rimlink.Server.Storage.Entities;
using Rimlink.Server.Tests.Support;
using Xunit;
using static Rimlink.Server.Tests.Support.GrpcTestExtensions;
using static Rimlink.Server.Tests.Support.RecoveryFixtures;

namespace Rimlink.Server.Tests.Economy;

public sealed class ParcelReceiptTests
{
    private static async Task<(LoginReply Login, long ParcelId)> Prepare(ServerFixture fixture)
    {
        await fixture.RegisterAndLogin("nova"); var login = await Login(fixture);
        using var scope = fixture.Services.CreateScope();
        var parcel = await scope.ServiceProvider.GetRequiredService<MailboxService>().Create(login.Player.PlayerId, null, ParcelKindDb.Admin,
            EconomyFixtures.Items(("Steel", 5)), null, 20, null, ignoreMailboxLimit: true);
        return (login, parcel.Id);
    }

    [Fact]
    public async Task AcknowledgementRequiresAStoredSnapshotContainingTheParcel()
    {
        using var fixture = EconomyFixtures.EconomyServer(); var p = await Prepare(fixture);
        var bytes = Save(100); var header = Header(bytes, 100);
        await SaveFixtures.Upload(new Protocol.V1.Saves.SavesClient(fixture.CreateChannel()), p.Login.SessionToken, bytes, header);
        var mailbox = new Protocol.V1.Mailbox.MailboxClient(fixture.CreateChannel());

        var error = await Assert.ThrowsAsync<RpcException>(() => mailbox.DeliverAsync(new DeliverRequest
            { ParcelId = p.ParcelId, GameTick = 100, SnapshotId = header.SnapshotId }, Bearer(p.Login.SessionToken)).ResponseAsync);

        Assert.Equal(ErrorCode.RecoveryRequired, error.ErrorDetail()?.Code);
        Assert.Single((await mailbox.ListAsync(new Empty(), Bearer(p.Login.SessionToken))).Parcels);
        Assert.Equal(0, (await EconomyFixtures.Clients(fixture, p.Login.SessionToken).Wallet.GetAsync(new Empty(), Bearer(p.Login.SessionToken))).Balance);
    }

    [Fact]
    public async Task TwoSavesOnTheSameTickHaveDifferentDeliveryProofs()
    {
        using var fixture = EconomyFixtures.EconomyServer(); var p = await Prepare(fixture);
        var saves = new Protocol.V1.Saves.SavesClient(fixture.CreateChannel());
        byte[] before = Save(100), after = Save(100, parcels: [p.ParcelId]);
        await SaveFixtures.Upload(saves, p.Login.SessionToken, before, Header(before, 100));
        var proof = Header(after, 100);
        await SaveFixtures.Upload(saves, p.Login.SessionToken, after, proof);
        var mailbox = new Protocol.V1.Mailbox.MailboxClient(fixture.CreateChannel());
        var request = new DeliverRequest { ParcelId = p.ParcelId, GameTick = 100, SnapshotId = proof.SnapshotId };
        await mailbox.DeliverAsync(request, Bearer(p.Login.SessionToken));
        await mailbox.DeliverAsync(request, Bearer(p.Login.SessionToken));
        Assert.Empty((await mailbox.ListAsync(new Empty(), Bearer(p.Login.SessionToken))).Parcels);
        using (var scope = fixture.Services.CreateScope())
            Assert.True(await scope.ServiceProvider.GetRequiredService<SaveStore>().Rollback(p.Login.Player.PlayerId, 1));

        Assert.True(Assert.Single((await mailbox.ListAsync(new Empty(), Bearer(p.Login.SessionToken))).Parcels).Redeliver);
        var fresh = await Login(fixture);
        byte[] recovered = Save(100, epoch: fresh.SaveWriteEpoch, parcels: [p.ParcelId]); var recoveredProof = Header(recovered, 100);
        await SaveFixtures.Upload(saves, fresh.SessionToken, recovered, recoveredProof);
        await mailbox.DeliverAsync(new DeliverRequest { ParcelId = p.ParcelId, GameTick = 100, SnapshotId = recoveredProof.SnapshotId,
            SaveWriteEpoch = fresh.SaveWriteEpoch, SaveGeneration = fresh.SaveGeneration }, Bearer(fresh.SessionToken));
        Assert.Empty((await mailbox.ListAsync(new Empty(), Bearer(fresh.SessionToken))).Parcels);
        Assert.Equal(20, (await EconomyFixtures.Clients(fixture, fresh.SessionToken).Wallet.GetAsync(new Empty(), Bearer(fresh.SessionToken))).Balance);
    }
}
