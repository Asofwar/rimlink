using System.IO;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Economy;
using Rimlink.Server.Application.Outposts;
using Xunit;

namespace Rimlink.Server.Tests.Outposts;

public sealed partial class OrdinaryReturnTests
{
    [Theory]
    [InlineData("BeforePending", false)][InlineData("AfterPending", false)]
    [InlineData("AfterDomainCommit", true)][InlineData("AfterExternalCommit", true)]
    public async Task Interrupted_ordinary_return_reopens_the_exact_negative_decision_not_a_second_attempt(string point, bool committed)
    {
        using var h = await CustodyHarness.Returned(); var original = Post(h); var cancel = await Removed(h, original);
        using var host = h.StartHost();
        using (var scope = host.Services.CreateScope())
        {
            var authority = scope.ServiceProvider.GetRequiredService<OutpostAuthority>();
            authority.CommitCheckpoint = current => { if (current == point) throw new IOException("ordinary-owned-stop"); };
            await Assert.ThrowsAsync<IOException>(() => scope.ServiceProvider.GetRequiredService<OrdinaryReturnService>().BeginReturnAsync(1, cancel, default));
            authority.CommitCheckpoint = null;
        }
        h.Db.ChangeTracker.Clear();
        Assert.Equal(committed ? 1 : 0, await h.Db.OutpostOrdinaryReturns.CountAsync());
        Assert.Equal(OutpostStartupState.Ready, (await h.Authority.OpenAsync(default)).State);
        using var reopened = host.Services.CreateScope();
        var returns = reopened.ServiceProvider.GetRequiredService<OrdinaryReturnService>();
        var observed = await returns.ResolveAsync(1, new() { Operation = cancel }, default);
        Assert.Equal(committed ? OrdinaryOperationStatus.ReturnStarted : OrdinaryOperationStatus.Unknown, observed.Status);
        var started = await returns.BeginReturnAsync(1, cancel, default);
        Assert.Equal(OrdinaryOperationStatus.ReturnStarted, started.Status);
        Assert.Equal(cancel.ReturnAttemptId, started.ReturnProof.TransitionId);
        Assert.Equal(cancel, started.ReturnProof.Authorization);
        Assert.Single(await h.Db.OutpostOrdinaryReturns.ToListAsync());
        Assert.Equal(started, await returns.BeginReturnAsync(1, cancel, default));
        var late = await Assert.ThrowsAsync<RpcException>(() => reopened.ServiceProvider.GetRequiredService<MarketService>().Post(1, original));
        Assert.Equal("ordinary-operation-return-fenced", late.Status.Detail);
        Assert.Empty(await h.Db.Lots.ToListAsync());
        Assert.Equal("LocalReturnPending", (await h.Row()).LocationKind);
        Assert.Equal(OutpostStartupState.Ready, (await h.Authority.OpenAsync(default)).State);
    }

    [Theory]
    [InlineData("BeforePending", false)][InlineData("AfterPending", false)]
    [InlineData("AfterDomainCommit", true)][InlineData("AfterExternalCommit", true)]
    public async Task Interrupted_returned_acceptance_imports_the_same_uuid_only_once_and_keeps_its_fence(string point, bool committed)
    {
        using var h = await CustodyHarness.Returned(); var original = Post(h); var cancel = await Removed(h, original);
        using var host = h.StartHost(); using var scope = host.Services.CreateScope();
        var returns = scope.ServiceProvider.GetRequiredService<OrdinaryReturnService>();
        var started = await returns.BeginReturnAsync(1, cancel, default);
        var home = await Returned(h, cancel, started);
        h.Authority.CommitCheckpoint = current => { if (current == point) throw new IOException("ordinary-owned-accept-stop"); };
        await Assert.ThrowsAsync<IOException>(() => h.F.F.H.Accept(home));
        h.Authority.CommitCheckpoint = null; h.Db.ChangeTracker.Clear();
        Assert.Equal(OutpostStartupState.Ready, (await h.Authority.OpenAsync(default)).State);
        var before = await returns.ResolveAsync(1, new() { Operation = cancel }, default);
        Assert.Equal(committed ? OrdinaryOperationStatus.ReturnAccepted : OrdinaryOperationStatus.ReturnStarted, before.Status);
        Assert.Equal(committed ? "Home" : "LocalReturnPending", (await h.Row()).LocationKind);
        if (!committed) await h.F.F.H.Accept(home); // Same already-written exact XML, not another physical spawn.
        long imported = (await h.Row()).Revision;
        var repeated = h.F.F.H.Snapshot(home.Recovery.Outposts.Metadata.Clone(), tick: 1002,
            removed: [cancel.Capture.LocalOperationId], returned: [cancel.Capture.LocalOperationId]);
        await h.F.F.H.Accept(repeated);
        Assert.Equal(imported, (await h.Row()).Revision);
        Assert.Equal(home.Version.SnapshotId, (await h.Db.OutpostOrdinaryReturns.AsNoTracking().SingleAsync()).AcceptedSnapshotId);
        Assert.Equal(OrdinaryOperationStatus.ReturnAccepted, (await returns.ResolveAsync(1, new() { Operation = cancel }, default)).Status);
        await Assert.ThrowsAsync<RpcException>(() => scope.ServiceProvider.GetRequiredService<MarketService>().Post(1, original));
        Assert.Empty(await h.Db.Lots.ToListAsync());
        Assert.Equal(OutpostStartupState.Ready, (await h.Authority.OpenAsync(default)).State);
    }
}
