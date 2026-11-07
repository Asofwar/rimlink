using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Auth;
using Rimlink.Server.Application.Economy;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Tests.Support;
using Xunit;

namespace Rimlink.Server.Tests.Outposts;

public sealed partial class OutpostCustodyBridgeTests
{
    [Fact]
    public async Task Pending_tagged_recipient_cannot_downgrade_before_first_custody_receipt()
    {
        using var h = await CustodyHarness.Returned();
        var outgoing = await PreparePendingRecipient(h);
        using var host = await h.StartHostAsync();
        using var scope = host.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<TransferService>().Send(1, outgoing);
        Assert.False(await h.Db.PawnCustody.AnyAsync(p => p.PlayerId == 2));
        Assert.False(await h.Db.OutpostSnapshotLinks.AnyAsync(p => p.PlayerId == 2));
        string originalToken = h.Sessions.ByPlayer(2)!.Token;
        var context = new LoginContext(ProtocolVersion.Current, null, [], "test")
        { RecoveryVersion = SaveRecoveryData.CurrentVersion, OutpostVersion = 0 };

        var result = await scope.ServiceProvider.GetRequiredService<AuthService>().LoginWithPassword("receiver", "pw", context);

        var failure = Assert.IsType<LoginResult.Failure>(result);
        Assert.Equal(ErrorCode.OutpostClientRequired, failure.Code);
        Assert.Equal(originalToken, h.Sessions.ByPlayer(2)!.Token);
        Assert.Equal(1, (await h.Row()).PlayerId);
    }

    [Fact]
    public async Task Pending_tagged_recipient_requires_protected_save_before_first_custody_receipt()
    {
        using var h = await CustodyHarness.Returned();
        var outgoing = await PreparePendingRecipient(h);
        using var host = await h.StartHostAsync();
        using var scope = host.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<TransferService>().Send(1, outgoing);

        Assert.True(await scope.ServiceProvider.GetRequiredService<OutpostSnapshotGuard>().RequiresProtectionAsync(2, default));
    }

    [Fact]
    public async Task Existing_legacy_session_cannot_fetch_pending_protected_physical_blobs()
    {
        using var h = await CustodyHarness.Returned();
        var outgoing = await PreparePendingRecipient(h);
        using var host = await h.StartHostAsync();
        using var scope = host.Services.CreateScope();
        var parcel = await scope.ServiceProvider.GetRequiredService<TransferService>().Send(1, outgoing);
        await scope.ServiceProvider.GetRequiredService<GiftService>().Answer(2, parcel.Id, true);
        var session = h.Sessions.ByPlayer(2)!;
        session.OutpostVersion = 0;
        var mailbox = new Mailbox.MailboxClient(host.CreateChannel());

        var error = await Assert.ThrowsAsync<RpcException>(() => mailbox.GetAsync(
            new ParcelRef { ParcelId = parcel.Id }, GrpcTestExtensions.Bearer(session.Token)).ResponseAsync);

        Assert.Equal(ErrorCode.OutpostClientRequired, error.ErrorDetail()?.Code);
        Assert.Equal(1, (await h.Row()).PlayerId);
        Assert.Null((await h.Db.Parcels.AsNoTracking().SingleAsync(p => p.Id == parcel.Id)).DeliveredAt);
    }

    private static async Task<TransferRequest> PreparePendingRecipient(CustodyHarness h)
    {
        h.AddRecipient(1);
        var account = h.Db.Accounts.Local.Single(a => a.Id == 2);
        account.Login = "receiver";
        account.PasswordHash = PasswordHasher.Hash("pw");
        await h.Db.SaveChangesAsync();
        var outgoing = new TransferRequest
        {
            ToPlayerId = 2, Items = new(), Pawns = new() { Pawns = { h.Blob.Clone() } },
            ClientRequestId = Guid.NewGuid().ToString("N"), GameTick = 1000
        };
        await h.Depart(outgoing);
        return outgoing;
    }
}
