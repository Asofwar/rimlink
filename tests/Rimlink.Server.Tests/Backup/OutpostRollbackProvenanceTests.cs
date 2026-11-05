using System.Security.Cryptography;
using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Application.Saves;
using Rimlink.Server.Tests.Outposts;
using Xunit;

namespace Rimlink.Server.Tests.Backup;

public sealed class OutpostRollbackProvenanceTests
{
    [Theory]
    [InlineData("capture")][InlineData("receipt")][InlineData("historical-operation")][InlineData("missing-root")]
    public async Task Changed_Q_echo_or_old_capture_is_not_authority(string damage)
    {
        using var f = await LifecycleFixture.Create();
        var actual = await f.H.Db.SaveVersions.AsNoTracking().SingleAsync(v => v.PlayerId == 1 && v.IsCurrent);
        var original = SaveStore.RecoveryOf(actual);
        await OutpostRestoreBridgeTests.Restore(f);
        using (var host = OutpostNestedRestoreTests.StartHost(f))
        {
            using var scope = host.Services.CreateScope();
            Assert.True(await scope.ServiceProvider.GetRequiredService<SaveStore>().Rollback(1, actual.Number));
        }
        var metadata = original.Outposts.Metadata.Clone();
        await using (var held = await f.H.F.H.H.Authority.AcquireAsync([1], default))
        {
            var current = await f.H.Db.SaveVersions.AsNoTracking().SingleAsync(v => v.PlayerId == 1 && v.IsCurrent);
            var bridges = await new OutpostRestoreEvidence(f.H.Db, f.H.F.H.H.Files).GetActiveDownloadBridgesHeldAsync(held, 1, current, default);
            metadata.RollbackBridge = null; metadata.RestoreBridge = null;
            metadata.RollbackFromViewBridge = Assert.IsType<OutpostRollbackFromViewBridge>(bridges.FromView);
            var active = OutpostSourceGraph.ValidateRoot(metadata.RollbackFromViewBridge);
            OutpostNestedRestoreTests.ApplyPath(metadata, metadata.RollbackFromViewBridge.SourceProvenance, active);
        }
        switch (damage)
        {
            case "capture": metadata.WorkerCaptures[0].Plants++; break;
            case "receipt": metadata.Receipts[0].IntentDigest = ByteString.CopyFrom(new byte[32]); break;
            case "historical-operation":
                var root = metadata.RollbackFromViewBridge;
                root.SourceProvenance.AuthorizedViews.Single(v => v.Kind == OutpostSourceAuthorizationKind.Restore).LifecycleOperationId = Guid.NewGuid().ToString("D");
                root.SourcePathDigest = ByteString.CopyFrom(SHA256.HashData(root.SourceProvenance.ToByteArray()));
                _ = OutpostSourceGraph.ValidateRoot(root); // Structurally legal is not independently committed authority.
                break;
            case "missing-root": metadata.RollbackFromViewBridge = null; metadata.WorkerCaptures.Clear(); metadata.Caravans.Clear(); break;
        }
        string before = await f.Cut();
        var saved = f.H.F.H.Snapshot(metadata, removed: original.Removed, returned: original.Returned);
        await Assert.ThrowsAsync<OutpostEvidenceUnavailableException>(() => f.H.F.H.Accept(saved));
        Assert.Equal(before, await f.Cut());
        Assert.Equal(new OutpostStartupResult(OutpostStartupState.Ready), await f.H.F.H.H.Authority.OpenAsync(default));
    }
}
