using System.Security.Cryptography;
using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Admin;
using Rimlink.Server.Application.Outposts;
using Xunit;

namespace Rimlink.Server.Tests.Outposts;

public sealed class OutpostAdminManifestTests
{
    [Fact]
    public async Task Preview_is_readonly_and_confirm_is_one_exact_audited_effect()
    {
        using var f = await Create(); var h = f.H;
        var staged = await f.Service.StageAsync(Actor.Api, await f.Document(), default);
        await OutpostMainObservationTests.Prepare(h, h.MainObservation);
        var service = OutpostAdminActivationTests.Service(h);
        var before = await Cut(h); var files = OutpostAdminInspectionTests.Files(h);
        var preview = await service.PreviewConfirmManifestAsync(Actor.Api, staged.ManifestId,
            Convert.FromHexString(staged.Digest), Guid.NewGuid().ToString("D"), "Confirm verified planet", default);
        var command = Assert.IsType<OutpostAdminCommand>(preview.Command);
        Assert.Empty(preview.Blockers); Assert.False(command.Confirmed);
        Assert.Equal(before, await Cut(h)); Assert.Equal(files, OutpostAdminInspectionTests.Files(h));
        Assert.Equal(staged.ManifestId, command.Intent.Manifest!.ManifestId);
        Assert.Equal(staged.Revision, command.Intent.Manifest.Revision);
        Assert.Equal(OutpostAdminIntentCodec.Digest(command.Intent), command.IntentDigest);
        var result = await service.ConfirmManifestAsync(Actor.Api, command with { Confirmed = true }, default);
        Assert.False(result.Replay); Assert.True(result.Outcome.NoReplayAuthorization);
        Assert.Equal(OutpostAdminDurableResultKind.ManifestConfirmed, result.Outcome.Kind);
        var row = await h.Db.OutpostManifests.AsNoTracking().SingleAsync();
        Assert.True(row.Confirmed && row.Active); Assert.Equal(command.Intent.Allocation.At, row.ConfirmedAt);
        Assert.Equal(staged.Digest, Convert.ToHexStringLower(row.Digest));
        var receipt = await h.Db.OutpostAdminCommands.AsNoTracking().SingleAsync(c => c.Action == "ConfirmManifest");
        Assert.Equal(command.Intent.Allocation.ReceiptId, receipt.Id);
        Assert.Equal(command.IntentDigest, receipt.CommandDigest);
        var transition = await h.Db.OutpostTransitions.AsNoTracking().SingleAsync(t => t.Id == receipt.TransitionId);
        Assert.Equal("AdminManifestConfirmed", transition.Kind);
        Assert.Equal(result.Head.Id, transition.NextHeadId);
        Assert.Equal((await OutpostDomainDigest.ComputeAsync(h.Db, OutpostDomainFormat.V2, default)).ToByteArray(), transition.Effects);
        Assert.Single(await h.Db.AdminActions.Where(a => a.Action == "outposts.ConfirmManifest").ToArrayAsync());
        Assert.Equal(new OutpostStartupResult(OutpostStartupState.Ready), await h.Authority.OpenAsync(default));
        Assert.False(h.Feature.IsAvailable);
    }

    [Theory]
    [InlineData("head")][InlineData("head-digest")][InlineData("revision")][InlineData("manifest-digest")]
    [InlineData("affected")][InlineData("evidence")][InlineData("intent-digest")][InlineData("confirmation")][InlineData("actor")]
    public async Task Changed_or_stale_manifest_command_has_no_effect(string defect)
    {
        using var f = await Create(); var command = await Preview(f); var intent = command.Intent;
        var expected = intent.Expected.Committed!; var selected = intent.Manifest!;
        if (defect == "head") expected = expected with { HeadId = Guid.NewGuid().ToString("D") };
        if (defect == "head-digest") expected.HeadDigest[0] ^= 1;
        if (defect == "revision") selected = selected with { Revision = selected.Revision + 1 };
        if (defect == "manifest-digest") selected.Digest[0] ^= 1;
        if (defect == "affected") selected.AffectedStateDigest[0] ^= 1;
        if (defect == "evidence") intent.Evidence.Single(e => e.Kind == OutpostAdminEvidenceKind.Manifest).Digest![0] ^= 1;
        intent = intent with { Expected = intent.Expected with { Committed = expected }, Manifest = selected };
        byte[] digest = OutpostAdminIntentCodec.Digest(intent);
        if (defect == "intent-digest") digest[0] ^= 1;
        command = new(intent, digest, defect != "confirmation");
        var before = await Cut(f.H);
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => OutpostAdminActivationTests.Service(f.H).ConfirmManifestAsync(
            defect == "actor" ? Actor.Api with { Name = "other admin" } : Actor.Api, command, default));
        Assert.Equal(before, await Cut(f.H));
        Assert.False((await f.H.Db.OutpostManifests.AsNoTracking().SingleAsync()).Confirmed);
    }

    [Fact]
    public async Task Exact_replay_returns_original_command_head_after_later_transitions_without_second_audit()
    {
        using var f = await Create(); var command = await Preview(f); var h = f.H;
        var service = OutpostAdminActivationTests.Service(h);
        var first = await service.ConfirmManifestAsync(Actor.Api, command, default);
        await Advance(h);
        string before = await Cut(h);
        var replay = await service.ConfirmManifestAsync(Actor.Api, command, default);
        Assert.True(replay.Replay); Assert.Equal(first.Head, replay.Head);
        Assert.Equal(OutpostRestoreEvidence.Encode(first.Outcome), OutpostRestoreEvidence.Encode(replay.Outcome));
        Assert.Equal(before, await Cut(h));
        Assert.Single(await h.Db.OutpostAdminCommands.Where(c => c.Action == "ConfirmManifest").ToArrayAsync());
        Assert.Single(await h.Db.AdminActions.Where(a => a.Action == "outposts.ConfirmManifest").ToArrayAsync());
    }

    [Fact]
    public async Task Racing_head_requires_new_preview_instead_of_silently_reconfirming()
    {
        using var f = await Create(); var command = await Preview(f); await Advance(f.H);
        string before = await Cut(f.H);
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => OutpostAdminActivationTests.Service(f.H).ConfirmManifestAsync(Actor.Api, command, default));
        Assert.Equal(before, await Cut(f.H));
    }

    [Theory]
    [InlineData("BeforePending", false)][InlineData("AfterPending", false)]
    [InlineData("AfterDomainCommit", true)][InlineData("AfterExternalCommit", true)]
    public async Task Interrupted_manifest_confirm_reconciles_one_effect_and_exact_original_receipt(string point, bool committed)
    {
        using var f = await Create(); var command = await Preview(f); var h = f.H;
        h.Authority.CommitCheckpoint = p => { if (p == point) throw new Stop(); };
        await Assert.ThrowsAsync<Stop>(() => OutpostAdminActivationTests.Service(h).ConfirmManifestAsync(Actor.Api, command, default));
        h.Authority.CommitCheckpoint = null; h.Db.ChangeTracker.Clear();
        Assert.Equal(new OutpostStartupResult(OutpostStartupState.Ready), await h.Authority.OpenAsync(default));
        Assert.Equal(committed, (await h.Db.OutpostManifests.AsNoTracking().SingleAsync()).Confirmed);
        var reply = await OutpostAdminActivationTests.Service(h).ConfirmManifestAsync(Actor.Api, command, default);
        Assert.Equal(committed, reply.Replay);
        Assert.Single(await h.Db.OutpostAdminCommands.Where(c => c.Action == "ConfirmManifest").ToArrayAsync());
        Assert.Single(await h.Db.AdminActions.Where(a => a.Action == "outposts.ConfirmManifest").ToArrayAsync());
    }

    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task Confirm_preserves_real_Q_F_custody_and_checkpoint_and_only_safety_holds_incompatible_outposts(bool incompatible)
    {
        using var f = await MutationHarness.Create(); var h = f.F.H.H;
        var service = OutpostAdminActivationTests.Service(h);
        await service.ActivateDomainFormatAsync(Actor.Api, await OutpostAdminActivationTests.CommandForCurrent(h), default);
        f.F.Time.Advance(TimeSpan.FromMinutes(31.5));
        await f.Service.StateAsync(1, new() { Scope = f.Scope.Clone() }, default);
        var before = await f.Row(); Assert.Equal(10, before.Stock);
        var source = await h.Db.OutpostManifests.AsNoTracking().SingleAsync(m => m.Id == before.ManifestId);
        var document = OutpostManifestDocument.Parser.ParseFrom(source.Document); document.ValuationVersion = "admin-replacement";
        if (incompatible) document.Tiles[0].NaturalTrees = false;
        var manifests = new OutpostManifestService(h.Db, h.Authority, new OutpostClock(f.F.Time), h.Files);
        var staged = await manifests.StageAsync(Actor.Api, document, default);
        await OutpostMainObservationTests.Prepare(h, h.MainObservation);
        var preview = await service.PreviewConfirmManifestAsync(Actor.Api, staged.ManifestId, Convert.FromHexString(staged.Digest),
            Guid.NewGuid().ToString("D"), "Replace the verified manifest without settling workers", default);
        var command = Assert.IsType<OutpostAdminCommand>(preview.Command) with { Confirmed = true };
        byte[] custody = OutpostRestoreEvidence.Encode(await h.Db.PawnCustody.AsNoTracking().OrderBy(p => p.Id).ToArrayAsync());
        byte[] assignments = OutpostRestoreEvidence.Encode(await h.Db.OutpostAssignments.AsNoTracking().OrderBy(a => a.Id).ToArrayAsync());
        var originalFiles = Directory.EnumerateFiles(h.Data.SavesRoot, "*", SearchOption.AllDirectories)
            .ToDictionary(path => path, path => SHA256.HashData(System.IO.File.ReadAllBytes(path)));
        f.F.Time.Advance(TimeSpan.FromDays(2));
        await service.ConfirmManifestAsync(Actor.Api, command, default);
        var after = await f.Row();
        Assert.Equal(incompatible ? "SafetyHold" : before.State, after.State);
        Assert.Equal(before.Revision + (incompatible ? 1 : 0), after.Revision);
        Assert.Equal(incompatible ? command.Intent.Allocation.TransitionId : before.LastTransitionId, after.LastTransitionId);
        // Compare every other stored outpost field; these are detached test copies, not database edits.
        after.State = before.State; after.Revision = before.Revision; after.LastTransitionId = before.LastTransitionId;
        Assert.Equal(OutpostRestoreEvidence.Encode(before), OutpostRestoreEvidence.Encode(after));
        Assert.Equal(custody, OutpostRestoreEvidence.Encode(await h.Db.PawnCustody.AsNoTracking().OrderBy(p => p.Id).ToArrayAsync()));
        Assert.Equal(assignments, OutpostRestoreEvidence.Encode(await h.Db.OutpostAssignments.AsNoTracking().OrderBy(a => a.Id).ToArrayAsync()));
        foreach (var pair in originalFiles) Assert.Equal(pair.Value, SHA256.HashData(System.IO.File.ReadAllBytes(pair.Key)));
        Assert.Equal(new OutpostStartupResult(OutpostStartupState.Ready), await h.Authority.OpenAsync(default));
    }

    private static async Task<ManifestFixture> Create()
    {
        var f = await ManifestFixture.Create();
        var command = await OutpostAdminActivationTests.CommandForCurrent(f.H);
        await OutpostAdminActivationTests.Service(f.H).ActivateDomainFormatAsync(Actor.Api, command, default);
        return f;
    }
    private static async Task<OutpostAdminCommand> Preview(ManifestFixture f)
    {
        var staged = await f.Service.StageAsync(Actor.Api, await f.Document(), default);
        await OutpostMainObservationTests.Prepare(f.H, f.H.MainObservation);
        var preview = await OutpostAdminActivationTests.Service(f.H).PreviewConfirmManifestAsync(Actor.Api,
            staged.ManifestId, Convert.FromHexString(staged.Digest), Guid.NewGuid().ToString("D"), "Confirm exact staged document", default);
        return Assert.IsType<OutpostAdminCommand>(preview.Command) with { Confirmed = true };
    }
    private static async Task Advance(AuthorityHarness h)
    {
        var row = await h.Db.OutpostAuthority.AsNoTracking().SingleAsync();
        await using var lease = await h.Authority.AcquireAsync([], default);
        await h.Authority.CommitHeldAsync(lease, new(Guid.NewGuid().ToString("D"), row.WorldId, 0, 0, row.RestoreBoundary,
            "Production", "fixture", new(row.HeadId, ByteString.CopyFrom(row.HeadDigest)), ByteString.Empty), (_, _) => Task.CompletedTask, default);
    }
    internal static async Task<string> Cut(AuthorityHarness h)
    {
        using var lease = await h.Anchor.AcquireAsync(default);
        return Convert.ToHexString(SHA256.HashData(OutpostRestoreEvidence.Encode(new {
            Domain = (await OutpostDomainDigest.ComputeAsync(h.Db, OutpostDomainFormat.V2, default)).ToByteArray(),
            Transitions = await h.Db.OutpostTransitions.AsNoTracking().OrderBy(t => t.Id).ToArrayAsync(),
            Audits = await h.Db.AdminActions.AsNoTracking().OrderBy(a => a.Id).ToArrayAsync(),
            Anchor = AnchorEnvelopeCodec.Encode(h.Anchor.ReadEnvelope(lease)!) })));
    }
    private sealed class Stop : Exception;
}
