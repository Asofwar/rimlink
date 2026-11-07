using System.Security.Cryptography;
using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Rimlink.Protocol;
using Rimlink.Server.Application.Economy;
using Rimlink.Server.Application.Events;
using Rimlink.Server.Application.Sessions;
using Rimlink.Server.Hosting;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Admin;
using Rimlink.Server.Application.Outposts;
using Xunit;

namespace Rimlink.Server.Tests.Outposts;

public sealed class OutpostAdminCancellationTests
{
    [Theory]
    [InlineData(true)][InlineData(false)]
    public async Task Accepted_proof_cancellation_is_one_admin_transition_and_preserves_the_original_business_request(bool noRemoval)
    {
        using var f = await Case.Create(noRemoval); var h = f.F.H.H;
        var original = await f.Operation(); var intent = (byte[])original.Intent.Clone(); var digest = (byte[])original.IntentDigest.Clone();
        var command = await f.Command(); string filesBefore = Files(f.F);
        var result = await OutpostAdminActivationTests.Service(h).ResolveAsync(Actor.Api, command, default);
        Assert.False(result.Replay); Assert.Equal(OutpostOperationStatus.Cancelled, result.Outcome.OutpostStatus);
        var operation = await f.Operation(); var raw = await h.Db.OutpostTransitions.AsNoTracking().SingleAsync(t => t.Id == operation.TransitionId);
        Assert.Equal(command.Intent.Allocation.TransitionId, raw.Id); Assert.Equal("AdminResolved", raw.Kind);
        Assert.Equal(1, raw.PlayerId); Assert.Equal(f.Selected.Scope.SaveGeneration, raw.SaveGeneration);
        Assert.Equal(OutpostAdminIntentCodec.ActorKey(Actor.Api), raw.Actor);
        Assert.Equal(intent, operation.Intent); Assert.Equal(digest, operation.IntentDigest); Assert.Equal("Cancelled", operation.State);
        Assert.Equal(0, operation.ReservedSilver); Assert.Equal(700, (await h.Db.Wallets.AsNoTracking().SingleAsync()).Balance);
        var row = await h.Db.Outposts.AsNoTracking().SingleAsync(); Assert.Equal("Closed", row.State); Assert.False(row.OccupiesSlot);
        var custody = await h.Db.PawnCustody.AsNoTracking().SingleAsync();
        Assert.Equal(noRemoval ? "Home" : "ReturnPending", custody.LocationKind); Assert.Equal(!noRemoval, custody.Tombstone);
        Assert.Equal(raw.Id, custody.FrontierTransitionId); Assert.Empty(await h.Db.OutpostReservations.Where(r => r.Active).ToArrayAsync());
        Assert.Empty(await h.Db.OutpostAssignments.Where(a => a.Active).ToArrayAsync());
        var parcels = await h.Db.OutpostParcelLinks.AsNoTracking().ToArrayAsync(); Assert.Equal(noRemoval ? 0 : 1, parcels.Length);
        if (!noRemoval)
        {
            var parcel = Assert.Single(parcels); Assert.Equal(raw.Id, parcel.TransitionId); Assert.Equal(raw.Id, parcel.BatchId);
            var payload = OutpostParcelPayload.Parser.ParseFrom(parcel.Payload);
            Assert.Equal(raw.Id, payload.BatchOperationId); Assert.Equal(f.F.Request.Scope, payload.Scope);
            Assert.Equal(f.F.Request.Workers.Select(w => w.PawnCustodyId), payload.PawnCustodyIds);
        }
        Assert.Equal(filesBefore, Files(f.F));
        var replay = await OutpostAdminActivationTests.Service(h).ResolveAsync(Actor.Api, command, default);
        Assert.True(replay.Replay); Assert.Equal(result.Head, replay.Head);
        var late = await f.F.Service.CommitAsync(1, f.F.Proof(f.Proof), default);
        Assert.Equal(OutpostOperationStatus.Cancelled, late.Status); Assert.Equal(raw.Id, late.TransitionId);
        Assert.Equal(ByteString.CopyFrom(raw.Digest), late.TransitionDigest);
        Assert.Single(await h.Db.OutpostAdminCommands.Where(c => c.Action == "Resolve").ToArrayAsync());
        Assert.Single(await h.Db.AdminActions.Where(a => a.Action == "outposts.Resolve").ToArrayAsync());
        Assert.Equal(new OutpostStartupResult(OutpostStartupState.Ready), await h.Authority.OpenAsync(default));
        Assert.False(h.Feature.IsAvailable);
    }

    [Theory]
    [InlineData("revision")][InlineData("state")][InlineData("request-digest")]
    [InlineData("canonical-hash")][InlineData("old-pre-proof")][InlineData("epoch")]
    public async Task Unproved_cancel_selection_is_readonly_and_never_makes_a_fence_or_return(string defect)
    {
        using var f = await Case.Create(true); var h = f.F.H.H; var selected = f.Selected;
        if (defect == "revision") selected = selected with { Revision = selected.Revision + 1 };
        if (defect == "state") selected = selected with { ExpectedState = "Prepared" };
        if (defect == "request-digest") selected.Original.IntentDigest[0] ^= 1;
        if (defect == "canonical-hash") selected.CanonicalSnapshot!.RawSha256 = ByteString.CopyFrom(new byte[32]);
        if (defect == "old-pre-proof") selected = selected with { CanonicalSnapshot = AssignmentHarness.SnapshotProof(f.F.Pre) };
        if (defect == "epoch") selected.Scope.SaveWriteEpoch++;
        string before = await OutpostAdminManifestTests.Cut(h);
        var preview = await OutpostAdminActivationTests.Service(h).PreviewResolveAsync(Actor.Api, selected,
            Guid.NewGuid().ToString("D"), "Do not guess missing removal evidence", default);
        Assert.Null(preview.Command); Assert.NotEmpty(preview.Blockers); Assert.Equal(before, await OutpostAdminManifestTests.Cut(h));
        Assert.Empty(await h.Db.OutpostAdminCommands.Where(c => c.Action == "Resolve").ToArrayAsync());
    }

    [Theory]
    [InlineData(true, "BeforePending", false)][InlineData(false, "BeforePending", false)]
    [InlineData(true, "AfterPending", false)][InlineData(false, "AfterPending", false)]
    [InlineData(true, "AfterDomainCommit", true)][InlineData(false, "AfterDomainCommit", true)]
    [InlineData(true, "AfterExternalCommit", true)][InlineData(false, "AfterExternalCommit", true)]
    public async Task Interrupted_admin_cancel_reopens_one_exact_effect_and_one_receipt(bool noRemoval, string point, bool committed)
    {
        using var f = await Case.Create(noRemoval); var h = f.F.H.H; var command = await f.Command();
        h.Authority.CommitCheckpoint = p => { if (p == point) throw new Stop(); };
        await Assert.ThrowsAsync<Stop>(() => OutpostAdminActivationTests.Service(h).ResolveAsync(Actor.Api, command, default));
        h.Authority.CommitCheckpoint = null; h.Db.ChangeTracker.Clear();
        Assert.Equal(new OutpostStartupResult(OutpostStartupState.Ready), await h.Authority.OpenAsync(default));
        var result = await OutpostAdminActivationTests.Service(h).ResolveAsync(Actor.Api, command, default);
        Assert.Equal(committed, result.Replay); Assert.Equal(OutpostOperationStatus.Cancelled, result.Outcome.OutpostStatus);
        Assert.Equal(noRemoval ? 0 : 1, await h.Db.OutpostParcelLinks.CountAsync());
        Assert.Single(await h.Db.OutpostAdminCommands.Where(c => c.Action == "Resolve").ToArrayAsync());
        Assert.Single(await h.Db.AdminActions.Where(a => a.Action == "outposts.Resolve").ToArrayAsync());
    }

    [Theory]
    [InlineData(true)][InlineData(false)]
    public async Task Unaccepted_cancellation_never_synthesizes_a_fence_or_return(bool noRemoval)
    {
        using var f = await Case.Create(noRemoval, acceptedProof: false); var h = f.F.H.H;
        string before = await OutpostAdminManifestTests.Cut(h);
        var preview = await OutpostAdminActivationTests.Service(h).PreviewResolveAsync(Actor.Api, f.Selected,
            Guid.NewGuid().ToString("D"), "No accepted cancellation proof exists", default);
        Assert.Null(preview.Command); Assert.NotEmpty(preview.Blockers);
        Assert.Equal(before, await OutpostAdminManifestTests.Cut(h));
        Assert.Equal("Prepared", (await f.Operation()).State);
        Assert.Empty(await h.Db.OutpostTransitions.Where(t => t.Kind == "CancelFence" || t.Kind == "AdminResolved").ToArrayAsync());
        Assert.Empty(await h.Db.OutpostParcelLinks.ToArrayAsync());
    }

    [Fact]
    public async Task Original_commit_wins_stale_admin_cancel_without_a_changed_intent_retry()
    {
        using var f = await Case.Create(false); var h = f.F.H.H; var command = await f.Command();
        var committed = await f.F.Service.CommitAsync(1, f.F.Proof(f.Proof), default);
        Assert.Equal(OutpostOperationStatus.Committed, committed.Status);
        string before = await OutpostAdminManifestTests.Cut(h);
        await Assert.ThrowsAsync<OutpostEvidenceUnavailableException>(() => OutpostAdminActivationTests.Service(h).ResolveAsync(Actor.Api, command, default));
        Assert.Equal(before, await OutpostAdminManifestTests.Cut(h));
        Assert.Empty(await h.Db.OutpostAdminCommands.Where(c => c.Action == "Resolve").ToArrayAsync());
        Assert.Empty(await h.Db.OutpostParcelLinks.ToArrayAsync());
    }

    [Fact]
    public async Task Admin_removed_return_uses_normal_delivery_and_accepted_receipt_only_once()
    {
        using var f = await Case.Create(false); var h = f.F.H.H;
        var command = await f.Command(); await OutpostAdminActivationTests.Service(h).ResolveAsync(Actor.Api, command, default);
        var parcel = await h.Db.OutpostParcelLinks.AsNoTracking().SingleAsync();
        var events = new EventBus(new SessionRegistry(), NullLogger<EventBus>.Instance);
        var mailbox = new MailboxService(h.Db, events, f.F.Wallet, Options.Create(new EconomyOptions()), h.Files);
        var service = new OutpostParcels(h.Db, h.Authority, f.F.Service, mailbox, new(f.F.Time), h.Files);
        var request = new OutpostDeliveryRequest { Scope = f.F.Request.Scope.Clone(), ClientRequestId = Guid.NewGuid().ToString("N"),
            ParcelId = parcel.ParcelId, PayloadDigest = ByteString.CopyFrom(parcel.PayloadDigest), CanonicalSnapshot = AssignmentHarness.SnapshotProof(f.Proof) };
        var started = await service.BeginDeliveryAsync(1, request, default);
        Assert.False(started.AckOnly); Assert.Equal(started, await service.BeginDeliveryAsync(1, request, default));
        var transition = await h.Db.OutpostTransitions.AsNoTracking().SingleAsync(t => t.Id == started.TransitionId);
        var metadata = f.Proof.Recovery.Outposts.Metadata.Clone(); metadata.Registry[0].Tombstone = false;
        metadata.Registry[0].PawnReferenceToken = "Thing_HumanReturned";
        metadata.Receipts.Add(new OutpostTransitionReceipt { TransitionId = transition.Id, PredecessorId = transition.PredecessorId ?? "",
            TransitionDigest = ByteString.CopyFrom(transition.Digest), ClientRequestId = request.ClientRequestId,
            IntentDigest = ByteString.CopyFrom(SHA256.HashData(request.ToByteArray())), Snapshot = request.CanonicalSnapshot.Clone(), DeliveryAttemptId = started.AttemptId,
            ParcelIds = { parcel.ParcelId }, PawnCustodyIds = { f.F.Request.Workers.Select(w => w.PawnCustodyId) },
            AssignmentIds = { f.F.Request.Workers.Select(w => w.AssignmentId) } });
        metadata.Frontier.Add(new OutpostFrontierEntry { TransitionId = transition.Id, PredecessorId = transition.PredecessorId ?? "",
            TransitionDigest = ByteString.CopyFrom(transition.Digest) });
        await f.F.H.Accept(f.F.H.Snapshot(metadata));
        Assert.True((await service.BeginDeliveryAsync(1, request, default)).AckOnly);
        Assert.Empty(await mailbox.Pending(1)); Assert.Equal("Home", (await h.Db.PawnCustody.AsNoTracking().SingleAsync()).LocationKind);
        long revision = (await h.Db.PawnCustody.AsNoTracking().SingleAsync()).Revision;
        await f.F.H.Accept(f.F.H.Snapshot(metadata));
        Assert.Equal(revision, (await h.Db.PawnCustody.AsNoTracking().SingleAsync()).Revision);
        Assert.Single(await h.Db.OutpostDeliveryAttempts.ToArrayAsync()); Assert.Single(await h.Db.OutpostParcelLinks.ToArrayAsync());
        Assert.Equal(new OutpostStartupResult(OutpostStartupState.Ready), await h.Authority.OpenAsync(default));
    }

    [Theory]
    [InlineData(true, true)][InlineData(false, true)][InlineData(true, false)][InlineData(false, false)]
    public async Task Existing_terminal_after_abort_is_descriptive_and_never_another_grant(bool noRemoval, bool adminAbort)
    {
        using var f = await Case.Create(noRemoval); var h = f.F.H.H;
        if (adminAbort) await OutpostAdminActivationTests.Service(h).ResolveAsync(Actor.Api, await f.Command(), default);
        else await f.F.Service.CancelAsync(1, f.F.Cancel(noRemoval ? OutpostCancelKind.AbortNoRemoval : OutpostCancelKind.ReturnAfterRemoval, f.Proof), default);
        string originalTransition = (await f.Operation()).TransitionId!;
        var selected = f.Selected with { Kind = OutpostAdminResolutionKind.ExistingTerminal, ExpectedState = "Cancelled",
            Revision = 2, CanonicalSnapshot = null };
        string before = await Business(f.F);
        var preview = await OutpostAdminActivationTests.Service(h).PreviewResolveAsync(Actor.Api, selected,
            Guid.NewGuid().ToString("D"), "Describe the existing cancellation without a fresh grant", default);
        var second = Assert.IsType<OutpostAdminCommand>(preview.Command) with { Confirmed = true };
        var result = await OutpostAdminActivationTests.Service(h).ResolveAsync(Actor.Api, second, default);
        Assert.Equal(OutpostOperationStatus.Cancelled, result.Outcome.OutpostStatus);
        Assert.Equal(before, await Business(f.F));
        var raw = await h.Db.OutpostTransitions.AsNoTracking().SingleAsync(t => t.Id == second.Intent.Allocation.TransitionId);
        var binding = OutpostAdminResolveEvidence.Read(raw);
        Assert.Equal(OutpostAdminBusinessSemantic.NoNewBusinessEffect, binding.Semantic);
        Assert.Equal(originalTransition, binding.NoEffect!.SourceTransitionId);
        Assert.Equal(originalTransition, (await f.Operation()).TransitionId);
        Assert.Equal(noRemoval ? 0 : 1, await h.Db.OutpostParcelLinks.CountAsync());
        Assert.Equal(new OutpostStartupResult(OutpostStartupState.Ready), await h.Authority.OpenAsync(default));
    }

    [Theory]
    [InlineData(true)][InlineData(false)]
    public async Task Admin_cancellation_stage_refuses_an_unowned_sqlite_transaction(bool noRemoval)
    {
        using var f = await Case.Create(noRemoval); var h = f.F.H.H;
        var command = await f.Command(); string before = await Business(f.F);
        Exception? failure;
        await using (var lease = await h.Authority.AcquireAsync([1], default))
        {
            await using var transaction = await h.Db.Database.BeginTransactionAsync();
            failure = await Record.ExceptionAsync(() => OutpostAssignmentService.StageAdminCancellationHeldAsync(h.Db, h.Files, lease,
                f.Selected, command.Intent.Allocation.TransitionId, command.Intent.Allocation.At, default));
            await transaction.RollbackAsync(); h.Db.ChangeTracker.Clear();
        }
        Assert.Equal("outpost-cancellation-requires-owned-transaction", Assert.IsType<InvalidOperationException>(failure).Message);
        Assert.Equal(before, await Business(f.F));
    }

    private static async Task<string> Business(AssignmentHarness f) => Convert.ToHexString(SHA256.HashData(OutpostRestoreEvidence.Encode(new {
        Operations = await f.Db.OutpostOperations.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(),
        Outposts = await f.Db.Outposts.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(),
        Custody = await f.Db.PawnCustody.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(),
        Assignments = await f.Db.OutpostAssignments.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(),
        Reservations = await f.Db.OutpostReservations.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(),
        Parcels = await f.Db.Parcels.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(),
        Links = await f.Db.OutpostParcelLinks.AsNoTracking().OrderBy(x => x.ParcelId).ToArrayAsync(),
        Snapshots = await f.Db.OutpostSnapshotLinks.AsNoTracking().OrderBy(x => x.WorldId).ThenBy(x => x.PlayerId).ThenBy(x => x.SaveGeneration).ThenBy(x => x.SnapshotId).ToArrayAsync(),
        Files = Files(f) })));

    private static string Files(AssignmentHarness f) => string.Join("\n", new[] { f.H.H.Data.SavesRoot, f.H.H.Data.Archive }
        .SelectMany(root => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)).Order(StringComparer.Ordinal)
        .Select(path => path + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))));
    private sealed class Stop : Exception;
    private sealed class Case(AssignmentHarness f, EvidenceSave proof, OutpostAdminResolutionSelection selected) : IDisposable
    {
        internal AssignmentHarness F => f;
        internal EvidenceSave Proof => proof;
        internal OutpostAdminResolutionSelection Selected => selected;
        internal Task<global::Rimlink.Server.Storage.Entities.OutpostOperation> Operation() => f.Db.OutpostOperations.AsNoTracking().SingleAsync();
        internal static async Task<Case> Create(bool noRemoval, bool acceptedProof = true)
        {
            var f = await AssignmentHarness.Create(); var prepared = await f.Service.PrepareAsync(1, f.Request, default);
            var accepted = !acceptedProof ? f.Pre : noRemoval ? await f.NoRemoval(await f.Service.CancelAsync(1, f.Cancel(OutpostCancelKind.AbortNoRemoval), default))
                : await f.Removal(prepared);
            var h = f.H.H; await OutpostAdminActivationTests.Service(h).ActivateDomainFormatAsync(Actor.Api,
                await OutpostAdminActivationTests.CommandForCurrent(h), default);
            var operation = await f.Db.OutpostOperations.AsNoTracking().SingleAsync();
            var selected = new OutpostAdminResolutionSelection(noRemoval ? OutpostAdminResolutionKind.AbortNoRemoval : OutpostAdminResolutionKind.AbortWithReturn,
                operation.Id, new(OutpostAdminOperationFamily.Outpost, operation.Kind, operation.RequestId, null, (byte[])operation.IntentDigest.Clone(), null),
                f.Request.Scope.Clone(), operation.State, 1, AssignmentHarness.SnapshotProof(accepted));
            await OutpostMainObservationTests.Prepare(h, h.MainObservation); return new(f, accepted, selected);
        }
        internal async Task<OutpostAdminCommand> Command() => Assert.IsType<OutpostAdminCommand>((await OutpostAdminActivationTests.Service(f.H.H)
            .PreviewResolveAsync(Actor.Api, selected, Guid.NewGuid().ToString("D"), "Use only accepted cancellation evidence", default)).Command) with { Confirmed = true };
        public void Dispose() => f.Dispose();
    }
}
