using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Application.Saves;
using Rimlink.Server.Storage;
using Rimlink.Server.Tests.Support;
using Xunit;

namespace Rimlink.Server.Tests.Outposts;

public sealed partial class OrdinaryReturnTests
{
    [Fact]
    public async Task Rollback_bridge_keeps_accepted_ordinary_return_historical_and_the_original_key_permanently_fenced()
    {
        using var h = await CustodyHarness.Returned(); var original = Post(h); var cancel = await Removed(h, original);
        using var host = h.StartHost(); using var services = host.Services.CreateScope(); var provider = services.ServiceProvider;
        var returns = provider.GetRequiredService<OrdinaryReturnService>();
        var started = await returns.BeginReturnAsync(1, cancel, default);
        var target = await Returned(h, cancel, started); await h.F.F.H.Accept(target);
        await h.F.F.H.Accept(h.F.F.H.Snapshot(target.Recovery.Outposts.Metadata.Clone(), tick: 2000,
            removed: target.Recovery.Removed, returned: target.Recovery.Returned));
        Assert.True(await provider.GetRequiredService<SaveStore>().Rollback(1, target.Version.Number));
        var metadata = target.Recovery.Outposts.Metadata.Clone();
        await using (var lease = await provider.GetRequiredService<OutpostAuthority>().AcquireAsync([1], default))
        {
            var bridge = (await new OutpostLifecycleEvidence(provider.GetRequiredService<RimlinkDbContext>(), provider.GetRequiredService<SaveFiles>())
                .GetRollbackBridgeHeldAsync(lease, 1, target.Version, default))!;
            metadata.Scope = bridge.CurrentScope.Clone(); metadata.RollbackBridge = bridge;
            foreach (var entry in bridge.MutableRegistry) metadata.Registry.Single(r => r.PawnCustodyId == entry.PawnCustodyId).Scope = metadata.Scope.Clone();
        }
        string custody = JsonSerializer.Serialize(await h.Db.PawnCustody.AsNoTracking().ToListAsync());
        var acceptedId = (await h.Db.OutpostOrdinaryReturns.AsNoTracking().SingleAsync()).AcceptedSnapshotId;
        await h.F.F.H.Accept(h.F.F.H.Snapshot(metadata, tick: 2001, removed: target.Recovery.Removed, returned: target.Recovery.Returned));
        Assert.Equal(custody, JsonSerializer.Serialize(await h.Db.PawnCustody.AsNoTracking().ToListAsync()));
        Assert.Equal(acceptedId, (await h.Db.OutpostOrdinaryReturns.AsNoTracking().SingleAsync()).AcceptedSnapshotId);
        var current = cancel.Clone(); current.Scope = metadata.Scope.Clone();
        await Assert.ThrowsAsync<global::Grpc.Core.RpcException>(() => provider.GetRequiredService<Rimlink.Server.Application.Economy.MarketService>().Post(1, original));
        Assert.Equal(new OutpostStartupResult(OutpostStartupState.Ready), await h.Authority.OpenAsync(default));
    }
}
