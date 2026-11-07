using System.Security.Cryptography;
using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Admin;
using Rimlink.Server.Application.Outposts;
using Xunit;

namespace Rimlink.Server.Tests.Outposts;

public sealed class OutpostAdminTerminalTests
{
    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task Existing_terminal_is_one_raw_admin_transition_without_a_second_business_effect(bool mutation)
    {
        using var f = await Create(mutation); var h = f.F.H.H;
        var operation = await Operation(f, mutation); var selection = Select(operation);
        var businessBefore = await Business(f); var fileBefore = GameFiles(h);
        var service = OutpostAdminActivationTests.Service(h);
        await OutpostMainObservationTests.Prepare(h, h.MainObservation);
        var before = await Cut(h); var allFilesBeforePreview = OutpostAdminInspectionTests.Files(h);
        var preview = await service.PreviewResolveAsync(Actor.Api, selection, Guid.NewGuid().ToString("D"), "Reference proved terminal outcome", default);
        var command = Assert.IsType<OutpostAdminCommand>(preview.Command) with { Confirmed = true };
        Assert.Empty(preview.Blockers); Assert.Equal(before, await Cut(h));
        Assert.Equal(allFilesBeforePreview, OutpostAdminInspectionTests.Files(h));
        var result = await service.ResolveAsync(Actor.Api, command, default);
        Assert.False(result.Replay); Assert.True(result.Outcome.NoReplayAuthorization);
        Assert.Equal(OutpostOperationStatus.Committed, result.Outcome.OutpostStatus);
        Assert.Equal(businessBefore, await Business(f)); Assert.Equal(fileBefore, GameFiles(h));
        var raw = await h.Db.OutpostTransitions.AsNoTracking().SingleAsync(t => t.Id == command.Intent.Allocation.TransitionId);
        Assert.Equal("AdminResolved", raw.Kind); Assert.Equal(selection.Scope.OwnerPlayerId, raw.PlayerId);
        Assert.Equal(selection.Scope.SaveGeneration, raw.SaveGeneration); Assert.Equal(OutpostAdminIntentCodec.ActorKey(Actor.Api), raw.Actor);
        Assert.NotEqual("player:1", raw.Actor); Assert.NotEqual(operation.Intent, raw.Intent);
        var source = await h.Db.OutpostTransitions.AsNoTracking().SingleAsync(t => t.Id == operation.TransitionId);
        Assert.NotEqual(source.Id, raw.Id); Assert.NotEqual(source.Digest, raw.Digest);
        Assert.Equal(result.Head.Id, raw.NextHeadId);
        Assert.Equal(command.IntentDigest, (await h.Db.OutpostAdminCommands.AsNoTracking().SingleAsync(c => c.Action == "Resolve")).CommandDigest);
        Assert.Single(await h.Db.AdminActions.Where(a => a.Action == "outposts.Resolve").ToArrayAsync());
        Assert.Equal(new OutpostStartupResult(OutpostStartupState.Ready), await h.Authority.OpenAsync(default));
        Assert.False(h.Feature.IsAvailable);
    }

    [Fact]
    public async Task Exact_terminal_command_replay_keeps_original_head_and_does_not_rewrite_outcome()
    {
        using var f = await Create(false); var h = f.F.H.H; var service = OutpostAdminActivationTests.Service(h);
        var command = await Preview(f, await Operation(f, false));
        var first = await service.ResolveAsync(Actor.Api, command, default);
        var head = await h.Db.OutpostAuthority.AsNoTracking().SingleAsync();
        await using (var lease = await h.Authority.AcquireAsync([], default))
            await h.Authority.CommitHeldAsync(lease, new(Guid.NewGuid().ToString("D"), head.WorldId, 0, 0, head.RestoreBoundary,
                "Production", "fixture", new(head.HeadId, ByteString.CopyFrom(head.HeadDigest)), ByteString.Empty), (_, _) => Task.CompletedTask, default);
        string before = await Cut(h); string business = await Business(f);
        var replay = await service.ResolveAsync(Actor.Api, command, default);
        Assert.True(replay.Replay); Assert.Equal(first.Head, replay.Head);
        Assert.Equal(OutpostRestoreEvidence.Encode(first.Outcome), OutpostRestoreEvidence.Encode(replay.Outcome));
        Assert.Equal(before, await Cut(h)); Assert.Equal(business, await Business(f));
        Assert.Single(await h.Db.OutpostAdminCommands.Where(c => c.Action == "Resolve").ToArrayAsync());
    }

    [Theory]
    [InlineData("revision")][InlineData("state")][InlineData("raw-intent-as-request-digest")]
    [InlineData("owner")][InlineData("epoch")][InlineData("boundary")][InlineData("request")][InlineData("source")]
    public async Task Substituted_terminal_selection_has_no_effect(string defect)
    {
        using var f = await Create(false); var h = f.F.H.H; var command = await Preview(f, await Operation(f, false));
        var selected = command.Intent.Resolution!; var original = selected.Original;
        if (defect == "revision") selected = selected with { Revision = selected.Revision + 1 };
        if (defect == "state") selected = selected with { ExpectedState = "Cancelled" };
        if (defect == "raw-intent-as-request-digest") original = original with { IntentDigest = SHA256.HashData((await Operation(f, false)).Intent) };
        if (defect == "owner") selected.Scope.OwnerPlayerId++;
        if (defect == "epoch") selected.Scope.SaveWriteEpoch++;
        if (defect == "boundary") selected.Scope.RestoreBoundary = Guid.NewGuid().ToString("D");
        if (defect == "request") original = original with { RequestId = Guid.NewGuid().ToString("D") };
        if (defect == "source") selected = selected with { OperationId = Guid.NewGuid().ToString("D") };
        selected = selected with { Original = original };
        var intent = command.Intent with { Resolution = selected };
        command = new(intent, OutpostAdminIntentCodec.Digest(intent), true);
        string before = await Cut(h);
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => OutpostAdminActivationTests.Service(h).ResolveAsync(Actor.Api, command, default));
        Assert.Equal(before, await Cut(h)); Assert.Empty(await h.Db.OutpostAdminCommands.Where(c => c.Action == "Resolve").ToArrayAsync());
    }

    [Theory]
    [InlineData("BeforePending", false)][InlineData("AfterPending", false)]
    [InlineData("AfterDomainCommit", true)][InlineData("AfterExternalCommit", true)]
    public async Task Interrupted_terminal_reference_selects_exact_old_or_new_admin_branch(string point, bool committed)
    {
        using var f = await Create(false); var h = f.F.H.H; var command = await Preview(f, await Operation(f, false));
        string business = await Business(f);
        h.Authority.CommitCheckpoint = p => { if (p == point) throw new Stop(); };
        await Assert.ThrowsAsync<Stop>(() => OutpostAdminActivationTests.Service(h).ResolveAsync(Actor.Api, command, default));
        h.Authority.CommitCheckpoint = null; h.Db.ChangeTracker.Clear();
        Assert.Equal(new OutpostStartupResult(OutpostStartupState.Ready), await h.Authority.OpenAsync(default));
        var result = await OutpostAdminActivationTests.Service(h).ResolveAsync(Actor.Api, command, default);
        Assert.Equal(committed, result.Replay); Assert.Equal(business, await Business(f));
        Assert.Single(await h.Db.OutpostAdminCommands.Where(c => c.Action == "Resolve").ToArrayAsync());
        Assert.Single(await h.Db.AdminActions.Where(a => a.Action == "outposts.Resolve").ToArrayAsync());
    }

    [Fact]
    public async Task Terminal_admin_reference_cannot_be_presented_as_a_player_materialization_receipt()
    {
        using var f = await Create(false); var h = f.F.H.H;
        var command = await Preview(f, await Operation(f, false));
        await OutpostAdminActivationTests.Service(h).ResolveAsync(Actor.Api, command, default);
        var raw = await h.Db.OutpostTransitions.AsNoTracking().SingleAsync(t => t.Id == command.Intent.Allocation.TransitionId);
        var metadata = f.Post.Recovery.Outposts.Metadata.Clone();
        metadata.Frontier.Add(new OutpostFrontierEntry { TransitionId = raw.Id, PredecessorId = raw.PredecessorId ?? "",
            TransitionDigest = ByteString.CopyFrom(raw.Digest) });
        metadata.Receipts.Add(new OutpostTransitionReceipt { TransitionId = raw.Id, PredecessorId = raw.PredecessorId ?? "",
            TransitionDigest = ByteString.CopyFrom(raw.Digest), ClientRequestId = command.Intent.RequestId,
            IntentDigest = ByteString.CopyFrom(command.IntentDigest), Snapshot = AssignmentHarness.SnapshotProof(f.Post) });
        var forged = f.F.H.Snapshot(metadata); string before = await Cut(h);
        var error = await Assert.ThrowsAsync<OutpostEvidenceUnavailableException>(() => f.F.H.Accept(forged));
        Assert.Equal("snapshot-receipt-kind-unavailable", error.Message); Assert.Equal(before, await Cut(h));
        Assert.Equal(new OutpostStartupResult(OutpostStartupState.Ready), await h.Authority.OpenAsync(default));
    }

    [Theory]
    [InlineData("generic-pointer")][InlineData("semantic")][InlineData("source")]
    [InlineData("result")][InlineData("raw-intent")][InlineData("request-digest")]
    public async Task Semantic_reader_requires_complete_exact_binding_not_an_admin_pointer(string defect)
    {
        using var f = await Create(false); var h = f.F.H.H;
        var command = await Preview(f, await Operation(f, false));
        await OutpostAdminActivationTests.Service(h).ResolveAsync(Actor.Api, command, default);
        var transitions = await h.Db.OutpostTransitions.AsNoTracking().ToDictionaryAsync(t => t.Id);
        var raw = transitions[command.Intent.Allocation.TransitionId]; var binding = OutpostAdminResolveEvidence.Read(raw);
        if (defect == "semantic") binding = binding with { Semantic = (OutpostAdminBusinessSemantic)2 };
        if (defect == "source") binding = binding with { NoEffect = binding.NoEffect with { SourceTransitionId = raw.Id } };
        if (defect == "result") binding.NoEffect.ResultSha256[0] ^= 1;
        if (defect == "raw-intent") binding.Intent.RawSha256[0] ^= 1;
        if (defect == "request-digest") binding.Original.RequestDigest[0] ^= 1;
        raw.Intent = defect == "generic-pointer" ? OutpostRestoreEvidence.Encode(new OutpostAdminTransitionBinding(2,
            binding.CommandId, binding.CommandDigest)) : OutpostRestoreEvidence.Encode(binding);
        string before = await Cut(h);
        var error = await Assert.ThrowsAsync<OutpostEvidenceUnavailableException>(() => OutpostAdminResolveEvidence.ReadValidatedAsync(h.Db, h.Files, raw, transitions, default));
        Assert.Equal("admin-resolution-evidence-mismatch", error.Message); Assert.Equal(before, await Cut(h));
    }

    [Fact]
    public async Task A_v2_target_cannot_be_misread_as_the_legacy_v1_business_selector()
    {
        using var f = await Create(false); var h = f.F.H.H; var service = OutpostAdminActivationTests.Service(h);
        await OutpostMainObservationTests.Prepare(h, h.MainObservation);
        var observation = await service.InspectAsync(Actor.Api, default);
        var intent = OutpostAdminRecoveryTargetTests.Ordinary() with { Actor = Actor.Api, Expected = observation };
        var command = new OutpostAdminCommand(intent, OutpostAdminIntentCodec.Digest(intent), true);
        string before = await Cut(h);
        await Assert.ThrowsAsync<OutpostEvidenceUnavailableException>(() => service.ResolveAsync(Actor.Api, command, default));
        Assert.Equal(before, await Cut(h));
        Assert.Empty(await h.Db.OutpostAdminCommands.Where(c => c.Action == "Resolve").ToArrayAsync());
    }

    private static async Task<MutationHarness> Create(bool mutation)
    {
        var f = await MutationHarness.Create(); var h = f.F.H.H;
        await OutpostAdminActivationTests.Service(h).ActivateDomainFormatAsync(Actor.Api, await OutpostAdminActivationTests.CommandForCurrent(h), default);
        if (mutation) { f.F.Time.Advance(TimeSpan.FromHours(1)); await f.Service.MutateAsync(1, f.Request(OutpostMutationKind.Claim), default); }
        return f;
    }
    private static Task<global::Rimlink.Server.Storage.Entities.OutpostOperation> Operation(MutationHarness f, bool mutation) => f.Db.OutpostOperations.AsNoTracking()
        .SingleAsync(o => o.Kind == (mutation ? "Claim" : "Found"));
    private static OutpostAdminResolutionSelection Select(global::Rimlink.Server.Storage.Entities.OutpostOperation operation)
    {
        var reply = OutpostOperationReply.Parser.ParseFrom(operation.Result);
        return new(OutpostAdminResolutionKind.ExistingTerminal, operation.Id,
            new(OutpostAdminOperationFamily.Outpost, operation.Kind, operation.RequestId, null, (byte[])operation.IntentDigest.Clone(), null),
            reply.Scope.Clone(), operation.State, reply.AuthoritativeRevision, null);
    }
    private static async Task<OutpostAdminCommand> Preview(MutationHarness f, global::Rimlink.Server.Storage.Entities.OutpostOperation operation)
    {
        var h = f.F.H.H; await OutpostMainObservationTests.Prepare(h, h.MainObservation);
        return Assert.IsType<OutpostAdminCommand>((await OutpostAdminActivationTests.Service(h).PreviewResolveAsync(Actor.Api, Select(operation),
            Guid.NewGuid().ToString("D"), "Reference proved terminal outcome", default)).Command) with { Confirmed = true };
    }
    private static async Task<string> Business(MutationHarness f) => Convert.ToHexString(SHA256.HashData(OutpostRestoreEvidence.Encode(new {
        Operations = await f.Db.OutpostOperations.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(),
        Outposts = await f.Db.Outposts.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(),
        Custody = await f.Db.PawnCustody.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(),
        Assignments = await f.Db.OutpostAssignments.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(),
        Reservations = await f.Db.OutpostReservations.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(),
        Parcels = await f.Db.Parcels.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(),
        ParcelLinks = await f.Db.OutpostParcelLinks.AsNoTracking().OrderBy(x => x.ParcelId).ToArrayAsync(),
        Snapshots = await f.Db.OutpostSnapshotLinks.AsNoTracking().OrderBy(x => x.WorldId).ThenBy(x => x.PlayerId).ThenBy(x => x.SaveGeneration).ThenBy(x => x.SnapshotId).ToArrayAsync(),
        Returns = await f.Db.OutpostOrdinaryReturns.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync() })));
    private static async Task<string> Cut(AuthorityHarness h)
    {
        using var lease = await h.Anchor.AcquireAsync(default);
        return Convert.ToHexString(SHA256.HashData(OutpostRestoreEvidence.Encode(new {
            Domain = (await OutpostDomainDigest.ComputeAsync(h.Db, OutpostDomainFormat.V2, default)).ToByteArray(),
            Transitions = await h.Db.OutpostTransitions.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(),
            Audits = await h.Db.AdminActions.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(), Anchor = AnchorEnvelopeCodec.Encode(h.Anchor.ReadEnvelope(lease)!) })));
    }
    private static string GameFiles(AuthorityHarness h) => string.Join("\n", new[] { h.Data.SavesRoot, h.Data.Archive }
        .SelectMany(root => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)).Order(StringComparer.Ordinal)
        .Select(path => path + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))));
    private sealed class Stop : Exception;
}
