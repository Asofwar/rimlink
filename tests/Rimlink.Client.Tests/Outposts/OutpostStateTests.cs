using Rimlink.Client.Outposts;
using Xunit;
using Rimlink.Protocol.V1;

namespace Rimlink.Client.Tests.Outposts;

public sealed partial class OutpostStateTests
{
    private static OutpostStateReply Snapshot(long revision = 12) => new() { Scope = OutpostReconciliationTests.Scope(),
        AuthoritativeRevision = revision, Outposts = { new OutpostSummary { OutpostId = "outpost-1", Stock = 20, Revision = 5 } },
        PendingOperations = { new OutpostOperationReply { ClientRequestId = "request-1", Status = OutpostOperationStatus.Prepared } } };

    [Fact]
    public void Read_model_is_detached_from_rpc_payload_and_callers()
    {
        var state = new OutpostState(); var reply = Snapshot(); state.Load(reply);
        reply.Scope.RestoreBoundary = "mutated"; reply.Outposts[0].Stock = 1000; reply.PendingOperations.Clear();
        Assert.Equal("boundary-1", state.Scope!.RestoreBoundary); Assert.Equal(20, Assert.Single(state.Outposts).Stock);
        Assert.Single(state.PendingOperations); state.Scope.RestoreBoundary = "caller"; state.Outposts[0].Stock = 999;
        state.PendingOperations[0].Status = OutpostOperationStatus.Committed;
        Assert.Equal("boundary-1", state.Scope.RestoreBoundary); Assert.Equal(20, state.Outposts[0].Stock);
        Assert.Equal(OutpostOperationStatus.Prepared, state.PendingOperations[0].Status);
    }

    [Fact]
    public void Complete_snapshot_keeps_pinned_manifest_and_is_detached_after_scope_reset()
    {
        var state = new OutpostState(); var source = Snapshot(); source.ManifestId = "manifest-1";
        state.Load(source); var view = state.Snapshot!;
        view.Scope.RestoreBoundary = "caller"; view.ManifestId = "wrong"; view.Outposts.Clear();
        Assert.Equal("boundary-1", state.Snapshot!.Scope.RestoreBoundary);
        Assert.Equal("manifest-1", state.Snapshot.ManifestId); Assert.Single(state.Snapshot.Outposts);
        state.Reset(); Assert.Null(state.Snapshot);
    }

    [Fact]
    public void Older_revision_cannot_overwrite_newer_preview()
    {
        var state = new OutpostState(); state.Load(Snapshot()); var old = Snapshot(11); old.Outposts[0].Stock = 10;
        state.Load(old); Assert.Equal(12, state.AuthoritativeRevision); Assert.Equal(20, state.Outposts[0].Stock);
    }

    [Fact]
    public void Different_boundary_requires_explicit_binding_and_clears_old_preview()
    {
        var state = new OutpostState(); state.Load(Snapshot()); var other = Snapshot(1); other.Scope.RestoreBoundary = "new-boundary";
        Assert.Throws<InvalidDataException>(() => state.Load(other)); Assert.Equal(20, state.Outposts[0].Stock);
        state.BindScope(other.Scope); Assert.Empty(state.Outposts); Assert.Empty(state.PendingOperations);
        state.Load(other); Assert.Equal("new-boundary", state.Scope!.RestoreBoundary); Assert.Equal(1, state.AuthoritativeRevision);
    }

    [Fact]
    public void Reset_clears_scope_and_preview_without_authorizing_pending_work()
    {
        var state = new OutpostState(); state.Load(Snapshot()); state.Reset(); Assert.Null(state.Scope);
        Assert.Empty(state.Outposts); Assert.Empty(state.PendingOperations); Assert.Equal(0, state.AuthoritativeRevision);
    }
}
