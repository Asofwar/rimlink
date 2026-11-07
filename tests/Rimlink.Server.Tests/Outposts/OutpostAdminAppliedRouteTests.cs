using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Admin;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Application.Raids;
using Rimlink.Server.Application.Saves;
using Rimlink.Server.Tests.Support;
using Xunit;

namespace Rimlink.Server.Tests.Outposts;

public sealed partial class OutpostCustodyBridgeTests
{
    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task Admin_Applied_reference_uses_actual_derived_live_raid_source_without_a_return(bool cancelled)
    {
        using var h = await CustodyHarness.Returned(); await PrepareRaidTarget(h); h.Sessions.ByPlayer(2)!.AtHome = true;
        var original = LiveFixtures.Propose(2, LiveKind.LiveRaid, 1, Guid.NewGuid().ToString("N"), 1100);
        original.Pawns = new() { Pawns = { h.Blob.Clone() } };
        var departure = await h.Depart(original);
        using var host = await h.StartHostAsync(); using var scope = host.Services.CreateScope();
        var raids = scope.ServiceProvider.GetRequiredService<RaidService>();
        var raid = await raids.Launch(1, OrdinaryCapture.DerivedLiveRaid(original));
        if (cancelled) await raids.Cancel(raid.Id, "Keep the original positive source after cancellation");
        var root = h.F.F.H.H;
        await OutpostUnitComposition.Activate(root);
        var captured = departure.OrdinaryCapture;
        var selected = new OutpostAdminRecoveryTarget(OutpostAdminRecoveryTargetKind.OrdinaryCapture,
            new(captured.LocalOperationId, new(OutpostAdminOperationFamily.Ordinary, captured.RequestKind.ToString(),
                captured.ClientRequestId, captured.LocalOperationId, captured.RequestIntentSha256.ToByteArray(), captured.DerivedRoute.Clone()),
                captured.Scope.Clone(), "Applied", 0, null, departure.RemovalSnapshot.Clone(), departure.RemovalSnapshot.Clone(), null));
        await OutpostMainObservationTests.Prepare(root, root.MainObservation);
        var service = OutpostAdminActivationTests.Service(root);
        string custody = Convert.ToHexString(OutpostRestoreEvidence.Encode(await h.Row()));
        var preview = await service.PreviewResolveAsync(Actor.Api, selected, null, Guid.NewGuid().ToString("D"), "Inspect the actual derived route", default);
        Assert.Empty(preview.Blockers);
        var command = Assert.IsType<OutpostAdminCommand>(preview.Command) with { Confirmed = true };
        var result = await service.ResolveAsync(Actor.Api, command, null, default);
        Assert.Equal(OrdinaryOperationStatus.Applied, result.Outcome.OrdinaryStatus); Assert.True(result.Outcome.NoReplayAuthorization);
        Assert.Empty(await h.Db.OutpostOrdinaryReturns.ToArrayAsync());
        Assert.Equal(custody, Convert.ToHexString(OutpostRestoreEvidence.Encode(await h.Row())));
        var raw = await h.Db.OutpostTransitions.AsNoTracking().SingleAsync(t => t.Id == command.Intent.Allocation.TransitionId);
        var binding = OutpostAdminResolveEvidence.Read(raw);
        Assert.Equal(raid.Id, binding.Applied!.SourceId); Assert.Equal(OrdinaryRequestKind.RaidLaunch, binding.Applied.Route);
        Assert.Equal(OutpostStartupState.Ready, (await root.Authority.OpenAsync(default)).State);
        string cut = await OutpostAdminManifestTests.Cut(root);
        Assert.True((await service.ResolveAsync(Actor.Api, command, null, default)).Replay);
        Assert.Equal(cut, await OutpostAdminManifestTests.Cut(root));
    }
}
