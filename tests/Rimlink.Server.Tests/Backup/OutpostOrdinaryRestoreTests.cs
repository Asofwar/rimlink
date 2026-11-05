using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Economy;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Backup;
using Rimlink.Server.Tests.Support;
using Xunit;

namespace Rimlink.Server.Tests.Outposts;

public sealed partial class OrdinaryReturnTests
{
    [Fact]
    public async Task Restore_keeps_accepted_ordinary_return_historical_and_original_business_keys_permanently_fenced()
    {
        using var h = await CustodyHarness.Returned(); var original = Post(h); var cancel = await Removed(h, original);
        using var host = h.StartHost();
        using (var services = host.Services.CreateScope())
        {
            var started = await services.ServiceProvider.GetRequiredService<OrdinaryReturnService>().BeginReturnAsync(1, cancel, default);
            await h.F.F.H.Accept(await Returned(h, cancel, started));
        }
        var target = await h.Db.SaveVersions.AsNoTracking().SingleAsync(v => v.PlayerId == 1 && v.IsCurrent);
        var recovered = Rimlink.Server.Application.Saves.SaveStore.RecoveryOf(target);
        string negative = JsonSerializer.Serialize(await h.Db.OutpostOrdinaryReturns.AsNoTracking().SingleAsync());
        var backup = await host.Services.GetRequiredService<BackupService>().CreateNow();
        var shared = h.F.F.H.H; var procedure = new OutpostRestoreProcedure(h.F.F.Time);
        var inspection = await procedure.InspectAsync(Path.Combine(shared.Data.Backups, backup.Name), shared.Data.Root,
            Path.GetDirectoryName(shared.Anchor.DatabasePath)!, default);
        var request = new RestoreRequest(Guid.NewGuid().ToString("D"), RestoreMode.ExistingInstallationRestore, inspection,
            inspection.Manifest.Authority.InstallationId, Guid.NewGuid().ToString("D"), [], "sqlite-local-v1", LocalTestStorage.LinuxQualification(shared.Data.Root));
        request = request with { ConfirmationDigest = OutpostRestoreJournalCodec.SelectionDigest(request) }; host.Dispose();
        var restored = await procedure.ExecuteAsync(request, default); Assert.True(restored.Outcome == RestoreOutcome.Completed, restored.Reason);
        h.Db.ChangeTracker.Clear(); var metadata = recovered.Outposts.Metadata.Clone();
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
        using var reopened = h.StartHost(); using var scope = reopened.Services.CreateScope();
        var changed = original.Clone(); changed.Pawn = null;
        var refusal = await Assert.ThrowsAsync<global::Grpc.Core.RpcException>(() => scope.ServiceProvider.GetRequiredService<MarketService>().Post(1, changed));
        Assert.Equal("ordinary-operation-return-fenced", refusal.Status.Detail); Assert.Empty(await h.Db.Lots.AsNoTracking().ToListAsync());
        Assert.Equal(new OutpostStartupResult(OutpostStartupState.Ready), await h.Authority.OpenAsync(default));
    }
}
