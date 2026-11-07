using System.Security.Cryptography;
using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Admin;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Application.Saves;
using Xunit;

namespace Rimlink.Server.Tests.Outposts;

public sealed class OutpostAdminOrdinaryTests
{
    [Fact]
    public async Task Admin_ordinary_return_uses_exact_ephemeral_input_and_one_owned_raw_transition()
    {
        using var f = await Case.Create(); var h = f.Root; string files = OutpostAdminInspectionTests.Files(h);
        string stable = await f.Stable(); long transitions = await h.Db.OutpostTransitions.CountAsync();
        var preview = await f.Service.PreviewResolveAsync(Actor.Api, f.Target, f.Input, Guid.NewGuid().ToString("D"), "Recover exact accepted ordinary removal", default);
        var command = Assert.IsType<OutpostAdminCommand>(preview.Command) with { Confirmed = true };
        Assert.Equal(files, OutpostAdminInspectionTests.Files(h)); Assert.Equal(stable, await f.Stable());
        Assert.Empty(await h.Db.OutpostOrdinaryReturns.ToArrayAsync());
        Assert.Equal(2, command.Intent.Version); Assert.Null(command.Intent.Resolution);
        var target = command.Intent.Recovery!.Ordinary!; Assert.Equal(f.Input.Capture.LocalOperationId, target.OperationId);
        Assert.Null(target.ExistingReturnAttemptId); Assert.Equal("Unknown", target.ExpectedState);
        Assert.Equal(f.Input.OriginalRequest.Length, target.Input!.OriginalLength);
        var result = await f.Service.ResolveAsync(Actor.Api, command, f.Input, default);
        Assert.False(result.Replay); Assert.Equal(OrdinaryOperationStatus.ReturnStarted, result.Outcome.OrdinaryStatus);
        var row = Assert.Single(await h.Db.OutpostOrdinaryReturns.AsNoTracking().ToArrayAsync());
        var authorization = OrdinaryCancelRequest.Parser.ParseFrom(row.Intent);
        Assert.Equal(command.Intent.Allocation.TransitionId, row.Id); Assert.Equal(row.Id, authorization.ReturnAttemptId);
        Assert.Equal(f.Input.OriginalRequest, authorization.OriginalRequest.ToByteArray());
        Assert.Equal(f.Input.Capture, authorization.Capture); Assert.Equal(f.Input.RemovalSnapshot, authorization.RemovalSnapshot);
        Assert.Equal(f.Input.CanonicalSnapshot, authorization.CanonicalSnapshot); Assert.Equal(target.Input.AuthorizationLength, row.Intent.Length);
        Assert.Equal(target.Input.AuthorizationDigest, SHA256.HashData(row.Intent));
        var raw = await h.Db.OutpostTransitions.AsNoTracking().SingleAsync(t => t.Id == row.Id);
        Assert.Equal("AdminResolved", raw.Kind); Assert.Equal(1, raw.PlayerId); Assert.Equal(f.Input.Capture.Scope.SaveGeneration, raw.SaveGeneration);
        Assert.Equal(OutpostAdminIntentCodec.ActorKey(Actor.Api), raw.Actor);
        Assert.Equal(transitions + 1, await h.Db.OutpostTransitions.CountAsync());
        Assert.Single(await h.Db.OutpostAdminCommands.Where(r => r.Action == "Resolve").ToArrayAsync());
        var custody = await f.H.Row(); Assert.Equal("LocalReturnPending", custody.LocationKind);
        Assert.Equal(row.Id, custody.LocationId); Assert.Equal(row.Id, custody.FrontierTransitionId); Assert.True(custody.Tombstone);
        Assert.Equal(stable, await f.Stable());
        Assert.Equal(new OutpostStartupResult(OutpostStartupState.Ready), await h.Authority.OpenAsync(default));
        var ordinary = new OrdinaryReturnService(h.Db, h.Authority, f.H.F.F.Service);
        var original = await ordinary.BeginReturnAsync(1, authorization, default);
        Assert.Equal(OrdinaryOperationStatus.ReturnStarted, original.Status); Assert.Equal(row.Id, original.ReturnProof.TransitionId);
        Assert.Equal(raw.Digest, original.ReturnProof.TransitionDigest.ToByteArray());
        string cut = await OutpostAdminManifestTests.Cut(h);
        var replay = await f.Service.ResolveAsync(Actor.Api, command, null, default);
        Assert.True(replay.Replay); Assert.Equal(result.Head, replay.Head); Assert.Equal(cut, await OutpostAdminManifestTests.Cut(h));
    }

    [Theory]
    [InlineData("missing")][InlineData("original")][InlineData("capture")][InlineData("removal")][InlineData("canonical")]
    [InlineData("commitment")][InlineData("actor")][InlineData("confirmed")][InlineData("head")]
    public async Task Changed_frozen_ordinary_input_or_command_cannot_install_a_negative_fence(string damage)
    {
        using var f = await Case.Create(); var command = await f.Command(); OriginalOrdinaryInput? input = f.Input;
        switch (damage)
        {
            case "missing": input = null; break;
            case "original": input.OriginalRequest[0] ^= 1; break;
            case "capture": input.Capture.ClientRequestId = Guid.NewGuid().ToString("N"); break;
            case "removal": input.RemovalSnapshot.RawSha256 = ByteString.CopyFrom(new byte[32]); break;
            case "canonical": input.CanonicalSnapshot.RawSha256 = ByteString.CopyFrom(new byte[32]); break;
            case "commitment": command.Intent.Recovery!.Ordinary!.Input!.AuthorizationDigest[0] ^= 1; command = command with { IntentDigest = OutpostAdminIntentCodec.Digest(command.Intent) }; break;
            case "actor": command = command with { Intent = command.Intent with { Actor = Actor.System }, IntentDigest = OutpostAdminIntentCodec.Digest(command.Intent with { Actor = Actor.System }) }; break;
            case "confirmed": command = command with { Confirmed = false }; break;
            case "head": command.Intent.Expected.Committed!.HeadDigest[0] ^= 1; command = command with { IntentDigest = OutpostAdminIntentCodec.Digest(command.Intent) }; break;
        }
        string cut = await OutpostAdminManifestTests.Cut(f.Root);
        var error = await Record.ExceptionAsync(() => f.Service.ResolveAsync(Actor.Api, command, input, default));
        Assert.True(error is OutpostEvidenceUnavailableException or InvalidDataException, error?.ToString() ?? "missing refusal");
        Assert.Equal(cut, await OutpostAdminManifestTests.Cut(f.Root)); Assert.Empty(await f.Root.Db.OutpostOrdinaryReturns.ToArrayAsync());
    }

    [Fact]
    public async Task A_winning_original_business_source_invalidates_the_frozen_negative_admin_command()
    {
        using var f = await Case.Create(sameCanonical: true); var command = await f.Command(); await f.H.Post(f.Departure);
        await OutpostMainObservationTests.Prepare(f.Root, f.Root.MainObservation); string cut = await OutpostAdminManifestTests.Cut(f.Root);
        var error = await Record.ExceptionAsync(() => f.Service.ResolveAsync(Actor.Api, command, f.Input, default));
        Assert.IsType<OutpostEvidenceUnavailableException>(error); Assert.Equal(cut, await OutpostAdminManifestTests.Cut(f.Root));
        Assert.Empty(await f.Root.Db.OutpostOrdinaryReturns.ToArrayAsync()); Assert.Single(await f.Root.Db.Lots.ToArrayAsync());
    }

    [Theory]
    [InlineData("unaccepted")][InlineData("not-current")][InlineData("custody")][InlineData("missing-input")]
    public async Task Unknown_alone_or_unmatched_accepted_evidence_is_not_admin_return_eligibility(string damage)
    {
        using var f = await Case.Create(); OriginalOrdinaryInput? input = f.Input; var target = f.Target;
        if (damage == "unaccepted")
        {
            var metadata = f.Canonical.Recovery.Outposts.Metadata.Clone(); var unaccepted = f.H.F.F.H.Snapshot(metadata, removed: [f.Input.Capture.LocalOperationId]);
            input = input with { CanonicalSnapshot = AssignmentHarness.SnapshotProof(unaccepted) }; target = target with { Ordinary = target.Ordinary! with { CanonicalSnapshot = input.CanonicalSnapshot.Clone() } };
        }
        if (damage == "not-current")
        {
            var later = f.H.F.F.H.Snapshot(f.Canonical.Recovery.Outposts.Metadata.Clone(), removed: [f.Input.Capture.LocalOperationId]); await f.H.F.F.H.Accept(later);
        }
        if (damage == "custody")
        {
            // A real competing ordinary authorization already owns the pending custody; Unknown cannot create another.
            var ordinary = new OrdinaryReturnService(f.Root.Db, f.Root.Authority, f.H.F.F.Service);
            await ordinary.BeginReturnAsync(1, OutpostAdminIntentCodec.ComposeOrdinaryAuthorization(input, Guid.NewGuid().ToString("D")), default);
        }
        if (damage == "missing-input") input = null;
        await OutpostMainObservationTests.Prepare(f.Root, f.Root.MainObservation); string cut = await OutpostAdminManifestTests.Cut(f.Root);
        var preview = await f.Service.PreviewResolveAsync(Actor.Api, target, input, Guid.NewGuid().ToString("D"), "Do not infer a return from Unknown", default);
        Assert.Null(preview.Command); Assert.NotEmpty(preview.Blockers); Assert.Equal(cut, await OutpostAdminManifestTests.Cut(f.Root));
    }

    [Fact]
    public async Task Accepted_admin_ordinary_receipt_is_ack_only_and_preserves_permanent_original_fence()
    {
        using var f = await Case.Create(); var command = await f.Command();
        await f.Service.ResolveAsync(Actor.Api, command, f.Input, default);
        var row = await f.Root.Db.OutpostOrdinaryReturns.AsNoTracking().SingleAsync();
        byte[] originalIntent = (byte[])row.Intent.Clone();
        var authorization = OrdinaryCancelRequest.Parser.ParseFrom(row.Intent);
        var ordinary = new OrdinaryReturnService(f.Root.Db, f.Root.Authority, f.H.F.F.Service);
        var started = await ordinary.BeginReturnAsync(1, authorization, default);
        var home = await OrdinaryReturnTests.Returned(f.H, authorization, started);
        await f.H.F.F.H.Accept(home);
        var custody = await f.H.Row();
        Assert.Equal("Home", custody.LocationKind); Assert.False(custody.Tombstone);
        long revision = custody.Revision;
        Assert.Equal(OrdinaryOperationStatus.ReturnAccepted, (await ordinary.BeginReturnAsync(1, authorization, default)).Status);
        var later = f.H.F.F.H.Snapshot(home.Recovery.Outposts.Metadata.Clone(), tick: 1002,
            removed: [authorization.Capture.LocalOperationId], returned: [authorization.Capture.LocalOperationId]);
        await f.H.F.F.H.Accept(later);
        Assert.Equal(revision, (await f.H.Row()).Revision);
        row = await f.Root.Db.OutpostOrdinaryReturns.AsNoTracking().SingleAsync();
        Assert.Equal("Accepted", row.State); Assert.Equal(home.Recovery.SnapshotId, row.AcceptedSnapshotId);
        Assert.Equal(originalIntent, row.Intent);
        var replay = await f.Service.ResolveAsync(Actor.Api, command, null, default);
        Assert.True(replay.Replay);
        await Assert.ThrowsAsync<global::Grpc.Core.RpcException>(() => OrdinaryReturnService.RequireNoFenceAsync(f.Root.Db, 1,
            authorization.Capture.RequestKind, authorization.Capture.ClientRequestId, default));
        Assert.Equal(new OutpostStartupResult(OutpostStartupState.Ready), await f.Root.Authority.OpenAsync(default));
    }

    [Theory]
    [InlineData("local-id")][InlineData("attempt")][InlineData("snapshot")][InlineData("custody")][InlineData("assignment")][InlineData("missing-returned")]
    public async Task Admin_ordinary_authorization_does_not_relax_accepted_return_receipt_checks(string fault)
    {
        using var f = await Case.Create(); var command = await f.Command();
        await f.Service.ResolveAsync(Actor.Api, command, f.Input, default);
        var row = await f.Root.Db.OutpostOrdinaryReturns.AsNoTracking().SingleAsync();
        var authorization = OrdinaryCancelRequest.Parser.ParseFrom(row.Intent);
        var ordinary = new OrdinaryReturnService(f.Root.Db, f.Root.Authority, f.H.F.F.Service);
        var started = await ordinary.BeginReturnAsync(1, authorization, default);
        var home = await OrdinaryReturnTests.Returned(f.H, authorization, started, fault != "missing-returned");
        var metadata = home.Recovery.Outposts.Metadata.Clone();
        var receipt = metadata.Receipts.Single(r => r.OrdinaryReturnAttemptId.Length > 0);
        switch (fault)
        {
            case "local-id": receipt.OrdinaryLocalOperationId = Guid.NewGuid().ToString("N"); break;
            case "attempt": receipt.OrdinaryReturnAttemptId = Guid.NewGuid().ToString("D"); break;
            case "snapshot": receipt.Snapshot.RawSha256 = ByteString.CopyFrom(new byte[32]); break;
            case "custody": receipt.PawnCustodyIds.Clear(); break;
            case "assignment": receipt.AssignmentIds.Clear(); break;
        }
        var rejected = fault == "missing-returned" ? home : f.H.F.F.H.Snapshot(metadata,
            removed: [authorization.Capture.LocalOperationId], returned: [authorization.Capture.LocalOperationId]);
        await Assert.ThrowsAsync<OutpostEvidenceUnavailableException>(() => f.H.F.F.H.Accept(rejected));
        Assert.Equal("LocalReturnPending", (await f.H.Row()).LocationKind);
        Assert.Equal("Started", (await f.Root.Db.OutpostOrdinaryReturns.AsNoTracking().SingleAsync()).State);
        Assert.Equal(new OutpostStartupResult(OutpostStartupState.Ready), await f.Root.Authority.OpenAsync(default));
    }

    [Theory]
    [InlineData("BeforePending", false)][InlineData("AfterPending", false)]
    [InlineData("AfterDomainCommit", true)][InlineData("AfterExternalCommit", true)]
    public async Task Interrupted_admin_ordinary_return_reopens_one_exact_authorization(string point, bool committed)
    {
        using var f = await Case.Create(); var command = await f.Command();
        f.Root.Authority.CommitCheckpoint = p => { if (p == point) throw new Stop(); };
        await Assert.ThrowsAsync<Stop>(() => f.Service.ResolveAsync(Actor.Api, command, f.Input, default));
        f.Root.Authority.CommitCheckpoint = null; f.Root.Db.ChangeTracker.Clear();
        Assert.Equal(new OutpostStartupResult(OutpostStartupState.Ready), await f.Root.Authority.OpenAsync(default));
        Assert.Equal(committed ? 1 : 0, await f.Root.Db.OutpostOrdinaryReturns.CountAsync());
        var result = await f.Service.ResolveAsync(Actor.Api, command, committed ? null : f.Input, default);
        Assert.Equal(committed, result.Replay);
        var row = Assert.Single(await f.Root.Db.OutpostOrdinaryReturns.AsNoTracking().ToArrayAsync());
        Assert.Equal(command.Intent.Allocation.TransitionId, row.Id);
        Assert.Single(await f.Root.Db.OutpostAdminCommands.Where(r => r.Action == "Resolve").ToArrayAsync());
        Assert.Single(await f.Root.Db.AdminActions.Where(r => r.Action == "outposts.Resolve").ToArrayAsync());
        Assert.Equal("LocalReturnPending", (await f.H.Row()).LocationKind);
    }

    [Theory]
    [InlineData(false, false)][InlineData(false, true)][InlineData(true, false)][InlineData(true, true)]
    public async Task Retained_ordinary_attempt_is_only_referenced_never_recreated_or_rebound(bool fromAdmin, bool accepted)
    {
        using var f = await Case.Create();
        var ordinary = new OrdinaryReturnService(f.Root.Db, f.Root.Authority, f.H.F.F.Service);
        if (fromAdmin) await f.Service.ResolveAsync(Actor.Api, await f.Command(), f.Input, default);
        else await ordinary.BeginReturnAsync(1, OutpostAdminIntentCodec.ComposeOrdinaryAuthorization(f.Input, Guid.NewGuid().ToString("D")), default);
        var row = await f.Root.Db.OutpostOrdinaryReturns.AsNoTracking().SingleAsync();
        var authorization = OrdinaryCancelRequest.Parser.ParseFrom(row.Intent);
        if (accepted)
        {
            var reply = await ordinary.BeginReturnAsync(1, authorization, default);
            await f.H.F.F.H.Accept(await OrdinaryReturnTests.Returned(f.H, authorization, reply));
        }
        await OutpostMainObservationTests.Prepare(f.Root, f.Root.MainObservation);
        row = await f.Root.Db.OutpostOrdinaryReturns.AsNoTracking().SingleAsync();
        string business = Convert.ToHexString(SHA256.HashData(OutpostRestoreEvidence.Encode(new { Row = row, Custody = await f.H.Row(), Stable = await f.Stable() })));
        var selected = f.Target with { Ordinary = f.Target.Ordinary! with { ExistingReturnAttemptId = row.Id,
            ExpectedState = accepted ? "ReturnAccepted" : "ReturnStarted", Input = null } };
        var preview = await f.Service.PreviewResolveAsync(Actor.Api, selected, null, Guid.NewGuid().ToString("D"), "Inspect the existing exact ordinary authorization only", default);
        var command = Assert.IsType<OutpostAdminCommand>(preview.Command) with { Confirmed = true };
        Assert.Null(command.Intent.Recovery!.Ordinary!.Input);
        var result = await f.Service.ResolveAsync(Actor.Api, command, null, default);
        Assert.Equal(accepted ? OrdinaryOperationStatus.ReturnAccepted : OrdinaryOperationStatus.ReturnStarted, result.Outcome.OrdinaryStatus);
        Assert.True(result.Outcome.NoReplayAuthorization);
        row = await f.Root.Db.OutpostOrdinaryReturns.AsNoTracking().SingleAsync();
        Assert.Equal(business, Convert.ToHexString(SHA256.HashData(OutpostRestoreEvidence.Encode(new { Row = row, Custody = await f.H.Row(), Stable = await f.Stable() }))));
        var raw = await f.Root.Db.OutpostTransitions.AsNoTracking().SingleAsync(t => t.Id == command.Intent.Allocation.TransitionId);
        var transitions = await f.Root.Db.OutpostTransitions.AsNoTracking().ToDictionaryAsync(t => t.Id);
        var view = await OutpostAdminResolveEvidence.ReadValidatedAsync(f.Root.Db, f.Root.Files, raw, transitions, default);
        Assert.Null(view.BusinessKind); Assert.Null(view.BusinessIntent);
        Assert.True((await f.Service.ResolveAsync(Actor.Api, command, null, default)).Replay);
        Assert.Equal(new OutpostStartupResult(OutpostStartupState.Ready), await f.Root.Authority.OpenAsync(default));
    }

    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task Retained_started_reference_survives_later_acceptance_without_renewing_authority(bool fromAdmin)
    {
        using var f = await Case.Create(); var selected = await f.RetainedTarget(fromAdmin);
        var command = Assert.IsType<OutpostAdminCommand>((await f.Service.PreviewResolveAsync(Actor.Api, selected, null,
            Guid.NewGuid().ToString("D"), "Record an existing Started authorization", default)).Command) with { Confirmed = true };
        var result = await f.Service.ResolveAsync(Actor.Api, command, null, default);
        var row = await f.Root.Db.OutpostOrdinaryReturns.AsNoTracking().SingleAsync(); byte[] intent = (byte[])row.Intent.Clone();
        var authorization = OrdinaryCancelRequest.Parser.ParseFrom(intent);
        var ordinary = new OrdinaryReturnService(f.Root.Db, f.Root.Authority, f.H.F.F.Service);
        var reply = await ordinary.BeginReturnAsync(1, authorization, default);
        await f.H.F.F.H.Accept(await OrdinaryReturnTests.Returned(f.H, authorization, reply));
        Assert.Equal(new OutpostStartupResult(OutpostStartupState.Ready), await f.Root.Authority.OpenAsync(default));
        string cut = await OutpostAdminManifestTests.Cut(f.Root);
        var replay = await f.Service.ResolveAsync(Actor.Api, command, null, default);
        Assert.True(replay.Replay); Assert.Equal(result.Head, replay.Head);
        Assert.Equal(OrdinaryOperationStatus.ReturnStarted, replay.Outcome.OrdinaryStatus); Assert.True(replay.Outcome.NoReplayAuthorization);
        Assert.Equal(cut, await OutpostAdminManifestTests.Cut(f.Root));
        row = await f.Root.Db.OutpostOrdinaryReturns.AsNoTracking().SingleAsync();
        Assert.Equal("Accepted", row.State); Assert.Equal(intent, row.Intent); Assert.Equal("Home", (await f.H.Row()).LocationKind);
        await Assert.ThrowsAsync<global::Grpc.Core.RpcException>(() => OrdinaryReturnService.RequireNoFenceAsync(f.Root.Db, 1,
            authorization.Capture.RequestKind, authorization.Capture.ClientRequestId, default));
    }

    [Theory]
    [InlineData("BeforePending", false)][InlineData("AfterPending", false)]
    [InlineData("AfterDomainCommit", true)][InlineData("AfterExternalCommit", true)]
    public async Task Interrupted_retained_reference_has_one_audit_and_no_business_effect(string point, bool committed)
    {
        using var f = await Case.Create(); var selected = await f.RetainedTarget(fromAdmin: false);
        var original = await f.Root.Db.OutpostOrdinaryReturns.AsNoTracking().SingleAsync();
        var custody = await f.H.Row();
        var command = Assert.IsType<OutpostAdminCommand>((await f.Service.PreviewResolveAsync(Actor.Api, selected, null,
            Guid.NewGuid().ToString("D"), "Reference without creating another return", default)).Command) with { Confirmed = true };
        f.Root.Authority.CommitCheckpoint = p => { if (p == point) throw new Stop(); };
        await Assert.ThrowsAsync<Stop>(() => f.Service.ResolveAsync(Actor.Api, command, null, default));
        f.Root.Authority.CommitCheckpoint = null; f.Root.Db.ChangeTracker.Clear();
        Assert.Equal(new OutpostStartupResult(OutpostStartupState.Ready), await f.Root.Authority.OpenAsync(default));
        Assert.Equal(committed ? 1 : 0, await f.Root.Db.OutpostAdminCommands.CountAsync(r => r.Action == "Resolve"));
        Assert.Equal(committed, (await f.Service.ResolveAsync(Actor.Api, command, null, default)).Replay);
        var after = Assert.Single(await f.Root.Db.OutpostOrdinaryReturns.AsNoTracking().ToArrayAsync());
        Assert.Equal(OutpostRestoreEvidence.Encode(original), OutpostRestoreEvidence.Encode(after));
        Assert.Equal(OutpostRestoreEvidence.Encode(custody), OutpostRestoreEvidence.Encode(await f.H.Row()));
        Assert.Single(await f.Root.Db.OutpostAdminCommands.Where(r => r.Action == "Resolve").ToArrayAsync());
        Assert.Single(await f.Root.Db.AdminActions.Where(r => r.Action == "outposts.Resolve").ToArrayAsync());
    }

    [Theory]
    [InlineData("state")][InlineData("attempt")][InlineData("request")][InlineData("local")]
    [InlineData("digest")][InlineData("scope")][InlineData("removal")][InlineData("canonical")][InlineData("input")]
    public async Task Retained_reference_rejects_substituted_selection_and_extraneous_input(string damage)
    {
        using var f = await Case.Create(); var selected = (await f.RetainedTarget(fromAdmin: false)).Ordinary!;
        OriginalOrdinaryInput? input = null;
        switch (damage)
        {
            case "state": selected = selected with { ExpectedState = "ReturnAccepted" }; break;
            case "attempt": selected = selected with { ExistingReturnAttemptId = Guid.NewGuid().ToString("D") }; break;
            case "request": selected = selected with { Original = selected.Original with { RequestId = Guid.NewGuid().ToString("N") } }; break;
            case "local": selected = selected with { OperationId = Guid.NewGuid().ToString("N") }; break;
            case "digest": selected.Original.IntentDigest[0] ^= 1; break;
            case "scope": selected.Scope.SaveWriteEpoch++; break;
            case "removal": selected.RemovalSnapshot.RawSha256 = ByteString.CopyFrom(new byte[32]); break;
            case "canonical": selected.CanonicalSnapshot.RawSha256 = ByteString.CopyFrom(new byte[32]); break;
            case "input":
                var proof = f.Input.CanonicalSnapshot.Clone(); proof.RawSha256 = ByteString.CopyFrom(new byte[32]);
                input = f.Input with { CanonicalSnapshot = proof }; break;
        }
        string cut = await OutpostAdminManifestTests.Cut(f.Root);
        var preview = await f.Service.PreviewResolveAsync(Actor.Api, new(OutpostAdminRecoveryTargetKind.OrdinaryCapture, selected), input,
            Guid.NewGuid().ToString("D"), "Do not substitute retained evidence", default);
        Assert.Null(preview.Command); Assert.NotEmpty(preview.Blockers); Assert.Equal(cut, await OutpostAdminManifestTests.Cut(f.Root));
    }

    private sealed class Stop : Exception;

    [Theory]
    [InlineData("source")][InlineData("attempt")][InlineData("intent")][InlineData("payload")]
    [InlineData("accepted")][InlineData("status")][InlineData("semantic")][InlineData("self")]
    public async Task Retained_reference_semantics_reject_changed_descriptors_and_source_ancestry(string damage)
    {
        using var f = await Case.Create(); var target = await f.RetainedTarget(fromAdmin: false);
        var command = Assert.IsType<OutpostAdminCommand>((await f.Service.PreviewResolveAsync(Actor.Api, target, null,
            Guid.NewGuid().ToString("D"), "Validate the exact retained source binding", default)).Command) with { Confirmed = true };
        await f.Service.ResolveAsync(Actor.Api, command, null, default);
        var raw = await f.Root.Db.OutpostTransitions.AsNoTracking().SingleAsync(t => t.Id == command.Intent.Allocation.TransitionId);
        var binding = OutpostAdminResolveEvidence.Read(raw); var reference = binding.Reference!;
        switch (damage)
        {
            case "source": reference.SourceTransitionDigest[0] ^= 1; break;
            case "attempt": reference = reference with { ReturnAttemptId = Guid.NewGuid().ToString("D") }; break;
            case "intent": reference.AuthorizationSha256[0] ^= 1; break;
            case "payload": reference.PayloadSha256[0] ^= 1; break;
            case "accepted": reference = reference with { AcceptedSnapshotId = Guid.NewGuid().ToString("N") }; break;
            case "status": reference = reference with { Status = OrdinaryOperationStatus.ReturnAccepted }; break;
            case "semantic": binding = binding with { Semantic = OutpostAdminBusinessSemantic.OrdinaryReturnStarted }; break;
            case "self": reference = reference with { SourceTransitionId = raw.Id }; break;
        }
        raw.Intent = OutpostRestoreEvidence.Encode(binding with { Reference = reference });
        var transitions = await f.Root.Db.OutpostTransitions.AsNoTracking().ToDictionaryAsync(t => t.Id);
        string cut = await OutpostAdminManifestTests.Cut(f.Root);
        await Assert.ThrowsAsync<OutpostEvidenceUnavailableException>(() => OutpostAdminResolveEvidence.ReadValidatedAsync(
            f.Root.Db, f.Root.Files, raw, transitions, default));
        Assert.Equal(cut, await OutpostAdminManifestTests.Cut(f.Root));
    }

    [Fact]
    public async Task Applied_ordinary_source_is_only_referenced_without_original_rpc_reconstruction()
    {
        using var f = await Case.Create(sameCanonical: true); await f.H.Post(f.Departure);
        await OutpostMainObservationTests.Prepare(f.Root, f.Root.MainObservation);
        var selected = f.Target with { Ordinary = f.Target.Ordinary! with { ExpectedState = "Applied", Input = null } };
        string stable = await f.Stable(); var custody = await f.H.Row();
        var preview = await f.Service.PreviewResolveAsync(Actor.Api, selected, null,
            Guid.NewGuid().ToString("D"), "Reference the proved positive source", default);
        Assert.Empty(preview.Blockers);
        var command = Assert.IsType<OutpostAdminCommand>(preview.Command) with { Confirmed = true };
        var result = await f.Service.ResolveAsync(Actor.Api, command, null, default);
        Assert.Equal(OrdinaryOperationStatus.Applied, result.Outcome.OrdinaryStatus); Assert.True(result.Outcome.NoReplayAuthorization);
        Assert.Empty(await f.Root.Db.OutpostOrdinaryReturns.ToArrayAsync());
        Assert.Equal(stable, await f.Stable()); Assert.Equal(OutpostRestoreEvidence.Encode(custody), OutpostRestoreEvidence.Encode(await f.H.Row()));
        var raw = await f.Root.Db.OutpostTransitions.AsNoTracking().SingleAsync(t => t.Id == command.Intent.Allocation.TransitionId);
        var transitions = await f.Root.Db.OutpostTransitions.AsNoTracking().ToDictionaryAsync(t => t.Id);
        var semantic = await OutpostAdminResolveEvidence.ReadValidatedAsync(f.Root.Db, f.Root.Files, raw, transitions, default);
        Assert.Null(semantic.BusinessKind); Assert.Null(semantic.BusinessIntent);
        Assert.Equal(new OutpostStartupResult(OutpostStartupState.Ready), await f.Root.Authority.OpenAsync(default));
        string cut = await OutpostAdminManifestTests.Cut(f.Root);
        var replay = await f.Service.ResolveAsync(Actor.Api, command, null, default);
        Assert.True(replay.Replay); Assert.Equal(result.Head, replay.Head); Assert.Equal(cut, await OutpostAdminManifestTests.Cut(f.Root));
    }

    [Theory]
    [InlineData("BeforePending", false)][InlineData("AfterPending", false)]
    [InlineData("AfterDomainCommit", true)][InlineData("AfterExternalCommit", true)]
    public async Task Interrupted_Applied_reference_keeps_the_original_positive_source(string point, bool committed)
    {
        using var f = await Case.Create(sameCanonical: true); await f.H.Post(f.Departure);
        await OutpostMainObservationTests.Prepare(f.Root, f.Root.MainObservation);
        var target = f.Target with { Ordinary = f.Target.Ordinary! with { ExpectedState = "Applied", Input = null } };
        var command = Assert.IsType<OutpostAdminCommand>((await f.Service.PreviewResolveAsync(Actor.Api, target, null,
            Guid.NewGuid().ToString("D"), "Reference an already applied business result", default)).Command) with { Confirmed = true };
        string stable = await f.Stable(); byte[] custody = OutpostRestoreEvidence.Encode(await f.H.Row());
        f.Root.Authority.CommitCheckpoint = p => { if (p == point) throw new Stop(); };
        await Assert.ThrowsAsync<Stop>(() => f.Service.ResolveAsync(Actor.Api, command, null, default));
        f.Root.Authority.CommitCheckpoint = null; f.Root.Db.ChangeTracker.Clear();
        Assert.Equal(OutpostStartupState.Ready, (await f.Root.Authority.OpenAsync(default)).State);
        Assert.Equal(committed, (await f.Service.ResolveAsync(Actor.Api, command, null, default)).Replay);
        Assert.Equal(stable, await f.Stable()); Assert.Equal(custody, OutpostRestoreEvidence.Encode(await f.H.Row()));
        Assert.Empty(await f.Root.Db.OutpostOrdinaryReturns.ToArrayAsync()); Assert.Single(await f.Root.Db.Lots.ToArrayAsync());
        Assert.Single(await f.Root.Db.OutpostAdminCommands.Where(r => r.Action == "Resolve").ToArrayAsync());
        Assert.Single(await f.Root.Db.AdminActions.Where(r => r.Action == "outposts.Resolve").ToArrayAsync());
    }

    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task Applied_requires_a_real_source_even_with_original_input(bool supplyInput)
    {
        using var f = await Case.Create();
        var target = f.Target with { Ordinary = f.Target.Ordinary! with { ExpectedState = "Applied", Input = null } };
        string cut = await OutpostAdminManifestTests.Cut(f.Root);
        var preview = await f.Service.PreviewResolveAsync(Actor.Api, target, supplyInput ? f.Input : null,
            Guid.NewGuid().ToString("D"), "Unknown cannot stand in for a positive source", default);
        Assert.Null(preview.Command); Assert.NotEmpty(preview.Blockers); Assert.Equal(cut, await OutpostAdminManifestTests.Cut(f.Root));
    }

    [Theory]
    [InlineData("source-id")][InlineData("source-digest")][InlineData("route")][InlineData("locator")]
    [InlineData("self")][InlineData("missing-ancestor")]
    public async Task Applied_reference_rejects_substituted_positive_source_binding(string damage)
    {
        using var f = await Case.Create(sameCanonical: true); await f.H.Post(f.Departure);
        await OutpostMainObservationTests.Prepare(f.Root, f.Root.MainObservation);
        var target = f.Target with { Ordinary = f.Target.Ordinary! with { ExpectedState = "Applied", Input = null } };
        var command = Assert.IsType<OutpostAdminCommand>((await f.Service.PreviewResolveAsync(Actor.Api, target, null,
            Guid.NewGuid().ToString("D"), "Bind only the actual positive source", default)).Command) with { Confirmed = true };
        await f.Service.ResolveAsync(Actor.Api, command, null, default);
        var raw = await f.Root.Db.OutpostTransitions.AsNoTracking().SingleAsync(t => t.Id == command.Intent.Allocation.TransitionId);
        var binding = OutpostAdminResolveEvidence.Read(raw); var applied = binding.Applied!;
        var transitions = await f.Root.Db.OutpostTransitions.AsNoTracking().ToDictionaryAsync(t => t.Id);
        switch (damage)
        {
            case "source-id": applied = applied with { SourceId = applied.SourceId + 1 }; break;
            case "source-digest": applied.SourceTransitionDigest[0] ^= 1; break;
            case "route": applied = applied with { Route = OrdinaryRequestKind.RaidLaunch }; break;
            case "locator": binding.Intent.RawSha256[0] ^= 1; break;
            case "self": applied = applied with { SourceTransitionId = raw.Id }; break;
            case "missing-ancestor": transitions.Remove(applied.SourceTransitionId); break;
        }
        raw.Intent = OutpostRestoreEvidence.Encode(binding with { Applied = applied });
        string cut = await OutpostAdminManifestTests.Cut(f.Root);
        await Assert.ThrowsAsync<OutpostEvidenceUnavailableException>(() => OutpostAdminResolveEvidence.ReadValidatedAsync(
            f.Root.Db, f.Root.Files, raw, transitions, default));
        Assert.Equal(cut, await OutpostAdminManifestTests.Cut(f.Root));
    }

    private sealed class Case(CustodyHarness h, CustodyTransferEvidence departure, EvidenceSave canonical, OriginalOrdinaryInput input) : IDisposable
    {
        internal CustodyHarness H => h; internal AuthorityHarness Root => h.F.F.H.H; internal EvidenceSave Canonical => canonical;
        internal CustodyTransferEvidence Departure => departure; internal OriginalOrdinaryInput Input => input;
        internal OutpostAdminService Service => OutpostAdminActivationTests.Service(Root);
        internal OutpostAdminRecoveryTarget Target => new(OutpostAdminRecoveryTargetKind.OrdinaryCapture, new(input.Capture.LocalOperationId,
            new(OutpostAdminOperationFamily.Ordinary, input.Capture.RequestKind.ToString(), input.Capture.ClientRequestId, input.Capture.LocalOperationId,
                input.Capture.RequestIntentSha256.ToByteArray(), input.Capture.DerivedRoute?.Clone()), input.Capture.Scope.Clone(), "Unknown", 0, null,
            input.RemovalSnapshot.Clone(), input.CanonicalSnapshot.Clone(), null));
        internal async Task<OutpostAdminCommand> Command() => Assert.IsType<OutpostAdminCommand>((await Service.PreviewResolveAsync(Actor.Api, Target, input,
            Guid.NewGuid().ToString("D"), "Recover the exact retained ordinary capture", default)).Command) with { Confirmed = true };
        internal async Task<OutpostAdminRecoveryTarget> RetainedTarget(bool fromAdmin)
        {
            if (fromAdmin) await Service.ResolveAsync(Actor.Api, await Command(), input, default);
            else await new OrdinaryReturnService(Root.Db, Root.Authority, H.F.F.Service).BeginReturnAsync(1,
                OutpostAdminIntentCodec.ComposeOrdinaryAuthorization(input, Guid.NewGuid().ToString("D")), default);
            await OutpostMainObservationTests.Prepare(Root, Root.MainObservation);
            var row = await Root.Db.OutpostOrdinaryReturns.AsNoTracking().SingleAsync();
            return Target with { Ordinary = Target.Ordinary! with { ExistingReturnAttemptId = row.Id, ExpectedState = "ReturnStarted", Input = null } };
        }
        internal static async Task<Case> Create(bool sameCanonical = false)
        {
            var h = await CustodyHarness.Returned();
            var request = new PostRequest { ClientRequestId = Guid.NewGuid().ToString("N"), Pawn = h.Blob.Clone(), Price = 150, GameTick = 1000 };
            var departure = await h.Depart(request); var current = await h.Db.SaveVersions.AsNoTracking().SingleAsync(v => v.PlayerId == 1 && v.IsCurrent);
            var canonical = h.F.F.H.Snapshot(SaveStore.RecoveryOf(current).Outposts.Metadata.Clone(), removed: [departure.OrdinaryCapture.LocalOperationId]);
            if (sameCanonical)
            {
                canonical = new(current, new SaveHeader(), SaveStore.RecoveryOf(current), []);
            }
            else await h.F.F.H.Accept(canonical);
            await OutpostAdminActivationTests.Service(h.F.F.H.H).ActivateDomainFormatAsync(Actor.Api, await OutpostAdminActivationTests.CommandForCurrent(h.F.F.H.H), default);
            await OutpostMainObservationTests.Prepare(h.F.F.H.H, h.F.F.H.H.MainObservation);
            return new(h, departure, canonical, new(1, request.ToByteArray(), departure.OrdinaryCapture.Clone(), departure.RemovalSnapshot.Clone(), AssignmentHarness.SnapshotProof(canonical)));
        }
        internal async Task<string> Stable() => Convert.ToHexString(SHA256.HashData(OutpostRestoreEvidence.Encode(new {
            Outposts = await Root.Db.Outposts.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(),
            Assignments = await Root.Db.OutpostAssignments.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(),
            Parcels = await Root.Db.Parcels.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(),
            Links = await Root.Db.OutpostSnapshotLinks.AsNoTracking().OrderBy(x => x.SnapshotId).ToArrayAsync(),
            Saves = await Root.Db.SaveVersions.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(),
            Files = Directory.EnumerateFiles(Root.Data.SavesRoot, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
                .Select(p => p + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p)))) })));
        public void Dispose() => h.Dispose();
    }
}
