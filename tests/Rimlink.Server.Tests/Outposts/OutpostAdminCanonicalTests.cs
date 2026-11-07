using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Rimlink.Server.Application.Admin;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Application.Saves;
using Rimlink.Server.Tests.Backup;
using Xunit;

namespace Rimlink.Server.Tests.Outposts;

public sealed class OutpostAdminCanonicalTests
{
    [Fact]
    public async Task Canonical_snapshot_without_business_operation_is_a_descriptive_requirement_even_after_a_later_save()
    {
        using var h = await SnapshotHarness.Create(); var save = h.Snapshot(); await h.Accept(save); var root = h.H;
        var service = OutpostAdminActivationTests.Service(root);
        await service.ActivateDomainFormatAsync(Actor.Api, await OutpostAdminActivationTests.CommandForCurrent(root), default);
        await OutpostMainObservationTests.Prepare(root, root.MainObservation);
        var scope = save.Recovery.Outposts.Metadata.Scope;
        var target = new OutpostAdminRecoveryTarget(OutpostAdminRecoveryTargetKind.CanonicalSnapshot, Canonical: new(save.Version.Id,
            save.Recovery.SnapshotId, scope.Clone(), scope.Clone(), root.Files.ReadEvidence(save.Version).Length,
            (byte[])save.Version.RawSha256!.Clone(), OutpostAdminSnapshotViewKind.DirectActual));
        string cut = await OutpostAdminManifestTests.Cut(root);
        var preview = await service.PreviewResolveAsync(Actor.Api, target, null, Guid.NewGuid().ToString("D"), "Require the existing canonical download", default);
        Assert.Empty(preview.Blockers);
        var command = Assert.IsType<OutpostAdminCommand>(preview.Command) with { Confirmed = true };
        Assert.Equal(cut, await OutpostAdminManifestTests.Cut(root));
        var result = await service.ResolveAsync(Actor.Api, command, null, default);
        Assert.Equal(OutpostAdminDurableResultKind.CanonicalDownloadRequired, result.Outcome.Kind);
        Assert.True(result.Outcome.NoReplayAuthorization); Assert.Null(result.Outcome.OrdinaryStatus); Assert.Null(result.Outcome.OutpostStatus);
        Assert.Empty(await root.Db.OutpostOperations.ToArrayAsync()); Assert.Empty(await root.Db.OutpostOrdinaryReturns.ToArrayAsync());
        Assert.Empty(await root.Db.OutpostDeliveryAttempts.ToArrayAsync()); Assert.Empty(await root.Db.OutpostLifecycleOperations.ToArrayAsync());
        Assert.Single(await root.Db.SaveVersions.ToArrayAsync());
        await h.Accept(h.Snapshot(tick: 1001));
        Assert.Equal(OutpostStartupState.Ready, (await root.Authority.OpenAsync(default)).State);
        cut = await OutpostAdminManifestTests.Cut(root);
        var replay = await service.ResolveAsync(Actor.Api, command, null, default);
        Assert.True(replay.Replay); Assert.Equal(result.Head, replay.Head); Assert.Equal(cut, await OutpostAdminManifestTests.Cut(root));
        await OutpostMainObservationTests.Prepare(root, root.MainObservation);
        var stale = await service.PreviewResolveAsync(Actor.Api, target, null, Guid.NewGuid().ToString("D"), "Reject a historical selection as today's canonical", default);
        Assert.Null(stale.Command); Assert.NotEmpty(stale.Blockers);
    }

    [Theory]
    [InlineData("row")][InlineData("snapshot")][InlineData("length")][InlineData("hash")]
    [InlineData("source-scope")][InlineData("view-scope")][InlineData("view-kind")]
    public async Task Canonical_substitution_never_becomes_a_download_or_business_grant(string damage)
    {
        using var h = await SnapshotHarness.Create(); var save = h.Snapshot(); await h.Accept(save); var root = h.H;
        var service = OutpostAdminActivationTests.Service(root);
        await service.ActivateDomainFormatAsync(Actor.Api, await OutpostAdminActivationTests.CommandForCurrent(root), default);
        var scope = save.Recovery.Outposts.Metadata.Scope;
        var target = new OutpostAdminCanonicalTarget(save.Version.Id, save.Recovery.SnapshotId, scope.Clone(), scope.Clone(),
            root.Files.ReadEvidence(save.Version).Length, save.Version.RawSha256!.ToArray(), OutpostAdminSnapshotViewKind.DirectActual);
        switch (damage)
        {
            case "row": target = target with { SaveVersionId = target.SaveVersionId + 1 }; break;
            case "snapshot": target = target with { SnapshotId = Guid.NewGuid().ToString("N") }; break;
            case "length": target = target with { RawLength = target.RawLength + 1 }; break;
            case "hash": target.RawDigest[0] ^= 1; break;
            case "source-scope": target.OriginalScope.SaveWriteEpoch++; break;
            case "view-scope": target.ViewScope.SaveWriteEpoch++; break;
            case "view-kind": target = target with { ViewKind = OutpostAdminSnapshotViewKind.AuthorizedView,
                ViewRootId = Guid.NewGuid().ToString("D"), ViewRootDigest = new byte[32], ViewPathDigest = new byte[32] }; break;
        }
        await OutpostMainObservationTests.Prepare(root, root.MainObservation); string cut = await OutpostAdminManifestTests.Cut(root);
        var preview = await service.PreviewResolveAsync(Actor.Api, new(OutpostAdminRecoveryTargetKind.CanonicalSnapshot, Canonical: target),
            null, Guid.NewGuid().ToString("D"), "Do not replace the selected canonical", default);
        Assert.Null(preview.Command); Assert.NotEmpty(preview.Blockers); Assert.Equal(cut, await OutpostAdminManifestTests.Cut(root));
    }

    [Theory]
    [InlineData("BeforePending", false)][InlineData("AfterPending", false)]
    [InlineData("AfterDomainCommit", true)][InlineData("AfterExternalCommit", true)]
    public async Task Interrupted_canonical_requirement_creates_only_one_receipt_and_audit(string point, bool committed)
    {
        using var h = await SnapshotHarness.Create(); var save = h.Snapshot(); await h.Accept(save); var root = h.H;
        var service = OutpostAdminActivationTests.Service(root);
        await service.ActivateDomainFormatAsync(Actor.Api, await OutpostAdminActivationTests.CommandForCurrent(root), default);
        var target = await new OutpostRestoreEvidence(root.Db, root.Files).DescribeAdminCanonicalAsync(save.Version,
            save.Recovery.Outposts.Metadata.Scope, null, default);
        var command = await Command(root, target); byte[] before = OutpostRestoreEvidence.Encode(await root.Db.SaveVersions.AsNoTracking().ToArrayAsync());
        root.Authority.CommitCheckpoint = p => { if (p == point) throw new Stop(); };
        await Assert.ThrowsAsync<Stop>(() => service.ResolveAsync(Actor.Api, command, null, default));
        root.Authority.CommitCheckpoint = null; root.Db.ChangeTracker.Clear();
        Assert.Equal(OutpostStartupState.Ready, (await root.Authority.OpenAsync(default)).State);
        Assert.Equal(committed, (await service.ResolveAsync(Actor.Api, command, null, default)).Replay);
        Assert.Equal(before, OutpostRestoreEvidence.Encode(await root.Db.SaveVersions.AsNoTracking().ToArrayAsync()));
        Assert.Single(await root.Db.OutpostAdminCommands.Where(c => c.Action == "Resolve").ToArrayAsync());
        Assert.Single(await root.Db.AdminActions.Where(a => a.Action == "outposts.Resolve").ToArrayAsync());
        Assert.Empty(await root.Db.OutpostOperations.ToArrayAsync()); Assert.Empty(await root.Db.OutpostOrdinaryReturns.ToArrayAsync());
    }

    [Fact]
    public async Task Canonical_receipt_remains_verifiable_after_archive_without_a_live_version_row()
    {
        using var f = await LifecycleFixture.Create(); var root = f.H.F.H.H; var service = OutpostAdminActivationTests.Service(root);
        await service.ActivateDomainFormatAsync(Actor.Api, await OutpostAdminActivationTests.CommandForCurrent(root), default);
        var version = await root.Db.SaveVersions.AsNoTracking().SingleAsync(v => v.PlayerId == 1 && v.IsCurrent);
        var target = await new OutpostRestoreEvidence(root.Db, root.Files).DescribeAdminCanonicalAsync(version,
            SaveStore.RecoveryOf(version).Outposts.Metadata.Scope, null, default);
        var command = await Command(root, target); await service.ResolveAsync(Actor.Api, command, null, default);
        await f.Archive(); Assert.Empty(await root.Db.SaveVersions.ToArrayAsync());
        Assert.Equal(OutpostStartupState.Ready, (await root.Authority.OpenAsync(default)).State);
        var raw = await root.Db.OutpostTransitions.AsNoTracking().SingleAsync(t => t.Id == command.Intent.Allocation.TransitionId);
        var view = await OutpostAdminResolveEvidence.ReadValidatedAsync(root.Db, root.Files, raw,
            await root.Db.OutpostTransitions.AsNoTracking().ToDictionaryAsync(t => t.Id), default);
        Assert.Null(view.BusinessKind); Assert.Null(view.BusinessIntent);
        Assert.Equal(target.SaveVersionId, OutpostAdminResolveEvidence.ReadCanonicalBinding(raw).Snapshot.SaveVersionId);
    }

    [Fact]
    public async Task Canonical_rollback_view_preserves_actual_scope_and_existing_provenance_root()
    {
        using var f = await LifecycleFixture.Create(runtimeQualified: true); var root = f.H.F.H.H; var old = f.H.Post;
        await f.H.F.H.Accept(f.H.F.H.Snapshot(old.Recovery.Outposts.Metadata.Clone(), tick: 2000));
        using (var scope = f.Host.Services.CreateScope())
            Assert.True(await scope.ServiceProvider.GetRequiredService<SaveStore>().Rollback(1, old.Version.Number));
        var service = OutpostAdminActivationTests.Service(root);
        var current = old.Recovery.Outposts.Metadata.Scope.Clone(); current.SaveWriteEpoch++;
        var version = await root.Db.SaveVersions.AsNoTracking().SingleAsync(v => v.PlayerId == 1 && v.IsCurrent);
        var selected = await new OutpostRestoreEvidence(root.Db, root.Files).DescribeAdminCanonicalAsync(version, current, null, default);
        Assert.Equal(OutpostAdminSnapshotViewKind.AuthorizedView, selected.ViewKind);
        Assert.Equal(0, selected.OriginalScope.SaveWriteEpoch); Assert.Equal(1, selected.ViewScope.SaveWriteEpoch);
        var operation = await root.Db.OutpostLifecycleOperations.AsNoTracking().SingleAsync();
        var progress = OutpostLifecycleEvidence.Decode<LifecycleProgress>(operation.Progress);
        Assert.Equal(progress.TransitionId, selected.ViewRootId);
        var command = await Command(root, selected); byte[] before = await Business(root);
        var result = await service.ResolveAsync(Actor.Api, command, null, default);
        Assert.Equal(OutpostAdminDurableResultKind.CanonicalDownloadRequired, result.Outcome.Kind); Assert.True(result.Outcome.NoReplayAuthorization);
        Assert.Equal(old.Version.RawSha256, selected.RawDigest);
        Assert.Single(await root.Db.OutpostLifecycleOperations.ToArrayAsync());
        Assert.Equal(OutpostStartupState.Ready, (await root.Authority.OpenAsync(default)).State);
        Assert.Equal(before, await Business(root));
        await f.Archive();
        Assert.Equal(OutpostStartupState.Ready, (await root.Authority.OpenAsync(default)).State);
    }

    [Fact]
    public async Task Canonical_change_between_preview_and_confirmation_requires_a_new_human_selection()
    {
        using var h = await SnapshotHarness.Create(); var save = h.Snapshot(); await h.Accept(save); var root = h.H;
        var service = OutpostAdminActivationTests.Service(root);
        await service.ActivateDomainFormatAsync(Actor.Api, await OutpostAdminActivationTests.CommandForCurrent(root), default);
        var selected = await new OutpostRestoreEvidence(root.Db, root.Files).DescribeAdminCanonicalAsync(save.Version,
            save.Recovery.Outposts.Metadata.Scope, null, default);
        var command = await Command(root, selected);
        await h.Accept(h.Snapshot(tick: 1001)); string cut = await OutpostAdminManifestTests.Cut(root);
        await Assert.ThrowsAsync<OutpostEvidenceUnavailableException>(() => service.ResolveAsync(Actor.Api, command, null, default));
        Assert.Equal(cut, await OutpostAdminManifestTests.Cut(root));
        Assert.Empty(await root.Db.OutpostAdminCommands.Where(c => c.Action == "Resolve").ToArrayAsync());
    }

    [Theory]
    [InlineData("source-scope")][InlineData("view-scope")][InlineData("length")][InlineData("raw-hash")]
    [InlineData("command")][InlineData("result")][InlineData("semantic")][InlineData("ancestry")]
    public async Task Canonical_historical_binding_cannot_substitute_source_view_result_or_ancestry(string damage)
    {
        using var h = await SnapshotHarness.Create(); var save = h.Snapshot(); await h.Accept(save); var root = h.H;
        var service = OutpostAdminActivationTests.Service(root);
        await service.ActivateDomainFormatAsync(Actor.Api, await OutpostAdminActivationTests.CommandForCurrent(root), default);
        var selected = await new OutpostRestoreEvidence(root.Db, root.Files).DescribeAdminCanonicalAsync(save.Version,
            save.Recovery.Outposts.Metadata.Scope, null, default);
        var command = await Command(root, selected); await service.ResolveAsync(Actor.Api, command, null, default);
        var raw = await root.Db.OutpostTransitions.AsNoTracking().SingleAsync(t => t.Id == command.Intent.Allocation.TransitionId);
        var binding = OutpostAdminResolveEvidence.ReadCanonicalBinding(raw);
        var transitions = await root.Db.OutpostTransitions.AsNoTracking().ToDictionaryAsync(t => t.Id);
        switch (damage)
        {
            case "source-scope": binding.Snapshot.OriginalScope.SaveWriteEpoch++; break;
            case "view-scope": binding.Snapshot.ViewScope.SaveWriteEpoch++; break;
            case "length": binding = binding with { Snapshot = binding.Snapshot with { RawLength = binding.Snapshot.RawLength + 1 } }; break;
            case "raw-hash": binding.Snapshot.RawDigest[0] ^= 1; break;
            case "command": binding.CommandDigest[0] ^= 1; break;
            case "result": binding.AdminResultSha256[0] ^= 1; break;
            case "semantic": binding = binding with { Semantic = OutpostAdminBusinessSemantic.OrdinaryReturnStarted }; break;
            case "ancestry": transitions.Remove(Guid.ParseExact(selected.SnapshotId, "N").ToString("D")); break;
        }
        raw.Intent = OutpostRestoreEvidence.Encode(binding); string cut = await OutpostAdminManifestTests.Cut(root);
        await Assert.ThrowsAsync<OutpostEvidenceUnavailableException>(() => OutpostAdminResolveEvidence.ReadValidatedAsync(root.Db, root.Files, raw, transitions, default));
        Assert.Equal(cut, await OutpostAdminManifestTests.Cut(root));
    }

    private static async Task<byte[]> Business(AuthorityHarness root) => OutpostRestoreEvidence.Encode(new {
        Saves = await root.Db.SaveVersions.AsNoTracking().OrderBy(v => v.Id).ToArrayAsync(),
        Outposts = await root.Db.Outposts.AsNoTracking().OrderBy(o => o.Id).ToArrayAsync(),
        Custody = await root.Db.PawnCustody.AsNoTracking().OrderBy(p => p.Id).ToArrayAsync(),
        Lifecycle = await root.Db.OutpostLifecycleOperations.AsNoTracking().OrderBy(o => o.Id).ToArrayAsync() });

    [Fact]
    public async Task Canonical_receipt_survives_bulk_restore_and_the_new_view_does_not_reauthorize_the_old_command()
    {
        using var f = await LifecycleFixture.Create(); var root = f.H.F.H.H; var service = OutpostAdminActivationTests.Service(root);
        await service.ActivateDomainFormatAsync(Actor.Api, await OutpostAdminActivationTests.CommandForCurrent(root), default);
        var version = await root.Db.SaveVersions.AsNoTracking().SingleAsync(v => v.PlayerId == 1 && v.IsCurrent);
        var original = SaveStore.RecoveryOf(version).Outposts.Metadata.Scope.Clone();
        var selected = await new OutpostRestoreEvidence(root.Db, root.Files).DescribeAdminCanonicalAsync(version, original, null, default);
        var command = await Command(root, selected); await service.ResolveAsync(Actor.Api, command, null, default);
        await root.MainObservation.DrainAsync(default);
        var (request, _) = await OutpostRestoreBridgeTests.Restore(f);
        Assert.Equal(OutpostStartupState.Ready, (await root.Authority.OpenAsync(default)).State);
        string cut = await OutpostAdminManifestTests.Cut(root);
        await Assert.ThrowsAsync<OutpostEvidenceUnavailableException>(() => service.ResolveAsync(Actor.Api, command, null, default));
        Assert.Equal(cut, await OutpostAdminManifestTests.Cut(root));
        version = await root.Db.SaveVersions.AsNoTracking().SingleAsync(v => v.PlayerId == 1 && v.IsCurrent);
        var current = original.Clone(); current.RestoreBoundary = request.TargetBoundary;
        var restored = await new OutpostRestoreEvidence(root.Db, root.Files).DescribeAdminCanonicalAsync(version, current, null, default);
        Assert.Equal(OutpostAdminSnapshotViewKind.AuthorizedView, restored.ViewKind);
        Assert.Equal(original, restored.OriginalScope); Assert.Equal(current, restored.ViewScope);
        Assert.Equal(request.RestoreId, restored.ViewRootId);
        byte[] before = await Business(root);
        var next = await service.ResolveAsync(Actor.Api, await Command(root, restored), null, default);
        Assert.Equal(OutpostAdminDurableResultKind.CanonicalDownloadRequired, next.Outcome.Kind); Assert.True(next.Outcome.NoReplayAuthorization);
        Assert.Equal(before, await Business(root));
        Assert.Equal(OutpostStartupState.Ready, (await root.Authority.OpenAsync(default)).State);
    }

    private static async Task<OutpostAdminCommand> Command(AuthorityHarness root, OutpostAdminCanonicalTarget target)
    {
        await OutpostMainObservationTests.Prepare(root, root.MainObservation);
        var preview = await OutpostAdminActivationTests.Service(root).PreviewResolveAsync(Actor.Api,
            new(OutpostAdminRecoveryTargetKind.CanonicalSnapshot, Canonical: target), null, Guid.NewGuid().ToString("D"), "Record the exact canonical requirement", default);
        Assert.Empty(preview.Blockers);
        return Assert.IsType<OutpostAdminCommand>(preview.Command) with { Confirmed = true };
    }

    private sealed class Stop : Exception;
}
