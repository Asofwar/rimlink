using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Economy;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Application.Saves;
using Rimlink.Server.Backup;
using Rimlink.Server.Tests.Support;
using Xunit;
using Wallet = Rimlink.Server.Storage.Entities.Wallet;

namespace Rimlink.Server.Tests.Outposts;

public sealed partial class OrdinaryReturnTests
{
    [Fact]
    public async Task Restored_ordinary_recipient_can_buy_before_its_first_protected_snapshot()
    {
        using var h = await CustodyHarness.Returned();
        h.AddRecipient(1);
        h.Db.Wallets.Add(new Wallet { PlayerId = 2, Balance = 1000 });
        await h.Db.SaveChangesAsync();
        using (var source = await h.StartHostAsync()) await RestoreWithBackup(h, source);

        var target = await h.Db.SaveVersions.AsNoTracking().SingleAsync(v => v.PlayerId == 1 && v.IsCurrent);
        var metadata = SaveStore.RecoveryOf(target).Outposts.Metadata.Clone();
        var shared = h.F.F.H.H;
        await using (var lease = await h.Authority.AcquireAsync([1], default))
        {
            var bridge = (await new OutpostRestoreEvidence(h.Db, shared.Files)
                .GetDownloadBridgeHeldAsync(lease, 1, target, default))!;
            var view = OutpostSourceGraph.ValidateRoot(bridge);
            metadata.Scope = view.EffectiveScope.Clone();
            metadata.RestoreBridge = bridge;
            var map = Assert.Single(view.RegistryMap);
            metadata.Registry[metadata.Registry.IndexOf(map.Before)] = map.After.Clone();
        }
        var purchase = Post(h);
        var registry = Assert.Single(metadata.Registry);
        purchase.Pawn.Registry = registry.Clone();
        var capture = OrdinaryCapture.Create(purchase, Guid.NewGuid().ToString("N"), metadata.Scope);
        metadata.OrdinaryOutgoing.Add(capture);
        registry.Tombstone = true;
        await h.F.F.H.Accept(h.F.F.H.Snapshot(metadata, removed: [capture.LocalOperationId]));

        using var host = await h.StartHostAsync();
        using var services = host.Services.CreateScope();
        var saves = services.ServiceProvider.GetRequiredService<SaveStore>();
        var seed = await h.Db.World.Select(w => w.Seed).SingleAsync();
        var xml = XDocument.Parse(Encoding.UTF8.GetString(SaveFixtures.Decompress(RecoveryFixtures.Save(2000))));
        xml.Root!.Element("game")!.Element("world")!.Element("info")!.Element("seedString")!.Value = seed;
        byte[] packed = SaveFixtures.Compress(Encoding.UTF8.GetBytes(xml.ToString()));
        async IAsyncEnumerable<ReadOnlyMemory<byte>> Chunks() { yield return packed; await Task.CompletedTask; }
        var ordinary = await saves.Store(2, RecoveryFixtures.Header(packed, 2000), Chunks(), default);
        Assert.Empty(await h.Db.OutpostSnapshotLinks.Where(l => l.PlayerId == 2).ToListAsync());
        var market = services.ServiceProvider.GetRequiredService<MarketService>();
        var lot = await market.Post(1, purchase);
        var delivered = await market.Buy(2, lot.Id);

        var pending = await h.Row();
        Assert.Equal((1L, "TransferPending", delivered.Id.ToString()), (pending.PlayerId, pending.LocationKind, pending.LocationId));
        Assert.Equal(1000 - purchase.Price, await h.Db.Wallets.Where(w => w.PlayerId == 2).Select(w => w.Balance).SingleAsync());
        Assert.Null((await h.Db.OutpostParcelLinks.AsNoTracking().SingleAsync(l => l.ParcelId == delivered.Id)).AcceptedSnapshotId);
        Assert.Empty(await h.Db.OutpostDeliveryAttempts.Where(a => a.ParcelId == delivered.Id).ToListAsync());
        Assert.Equal(new OutpostStartupResult(OutpostStartupState.Ready), await h.Authority.OpenAsync(default));

        await h.Db.SaveVersions.Where(v => v.Id == ordinary.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(v => v.SnapshotId, Guid.NewGuid().ToString("N")));
        Assert.Equal(OutpostStartupState.Maintenance, (await h.Authority.OpenAsync(default)).State);
    }

    [Fact]
    public async Task Restore_keeps_accepted_ordinary_return_historical_and_original_business_keys_permanently_fenced()
    {
        using var h = await CustodyHarness.Returned(); var original = Post(h); var cancel = await Removed(h, original);
        using var host = await h.StartHostAsync();
        using (var services = host.Services.CreateScope())
        {
            var started = await services.ServiceProvider.GetRequiredService<OrdinaryReturnService>().BeginReturnAsync(1, cancel, default);
            await h.F.F.H.Accept(await Returned(h, cancel, started));
        }
        var target = await h.Db.SaveVersions.AsNoTracking().SingleAsync(v => v.PlayerId == 1 && v.IsCurrent);
        var recovered = Rimlink.Server.Application.Saves.SaveStore.RecoveryOf(target);
        string negative = JsonSerializer.Serialize(await h.Db.OutpostOrdinaryReturns.AsNoTracking().SingleAsync());
        await RestoreWithBackup(h, host);
        var shared = h.F.F.H.H; var metadata = recovered.Outposts.Metadata.Clone();
        await using (var lease = await shared.Authority.AcquireAsync([1], default))
        {
            var bridge = (await new OutpostRestoreEvidence(h.Db, shared.Files).GetDownloadBridgeHeldAsync(lease, 1, target, default))!;
            var view = OutpostSourceGraph.ValidateRoot(bridge); metadata.Scope = view.EffectiveScope.Clone(); metadata.RestoreBridge = bridge;
            foreach (var map in view.RegistryMap)
                metadata.Registry[metadata.Registry.IndexOf(metadata.Registry.Single(e => e.Equals(map.Before)))] = map.After.Clone();
        }
        string custody = JsonSerializer.Serialize(await h.Db.PawnCustody.AsNoTracking().ToListAsync());
        await h.F.F.H.Accept(h.F.F.H.Snapshot(metadata, removed: recovered.Removed, returned: recovered.Returned));
        await h.F.F.H.Accept(h.F.F.H.Snapshot(metadata.Clone(), removed: recovered.Removed, returned: recovered.Returned));
        Assert.Equal(custody, JsonSerializer.Serialize(await h.Db.PawnCustody.AsNoTracking().ToListAsync()));
        Assert.Equal(negative, JsonSerializer.Serialize(await h.Db.OutpostOrdinaryReturns.AsNoTracking().SingleAsync()));
        using var reopened = await h.StartHostAsync(); using var scope = reopened.Services.CreateScope();
        var changed = original.Clone(); changed.Pawn = null;
        var refusal = await Assert.ThrowsAsync<global::Grpc.Core.RpcException>(() => scope.ServiceProvider.GetRequiredService<MarketService>().Post(1, changed));
        Assert.Equal("ordinary-operation-return-fenced", refusal.Status.Detail); Assert.Empty(await h.Db.Lots.AsNoTracking().ToListAsync());
        Assert.Equal(new OutpostStartupResult(OutpostStartupState.Ready), await h.Authority.OpenAsync(default));
    }

    private static async Task RestoreWithBackup(CustodyHarness h, ServerFixture host)
    {
        var backup = await host.Services.GetRequiredService<BackupService>().CreateNow();
        var shared = h.F.F.H.H; var procedure = new OutpostRestoreProcedure(h.F.F.Time);
        var inspection = await procedure.InspectAsync(Path.Combine(shared.Data.Backups, backup.Name), shared.Data.Root,
            Path.GetDirectoryName(shared.Anchor.DatabasePath)!, default);
        var request = new RestoreRequest(Guid.NewGuid().ToString("D"), RestoreMode.ExistingInstallationRestore, inspection,
            inspection.Manifest.Authority.InstallationId, Guid.NewGuid().ToString("D"), [], "sqlite-local-v1", LocalTestStorage.LinuxQualification(shared.Data.Root));
        request = request with { ConfirmationDigest = OutpostRestoreJournalCodec.SelectionDigest(request) };
        await host.DisposeAsync();
        var restored = await procedure.ExecuteAsync(request, default);
        Assert.Equal((RestoreOutcome.Completed, ""), (restored.Outcome, restored.Reason));
        h.Db.ChangeTracker.Clear();
    }
}
