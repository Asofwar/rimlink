using System.Security.Cryptography;
using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Rimlink.Server.Application.Admin;
using Rimlink.Server.Application.Outposts;
using Xunit;

namespace Rimlink.Server.Tests.Outposts;

public sealed class OutpostAdminBootstrapTests
{
    [Fact]
    public async Task Confirmed_initialize_is_one_v2_sql_transaction_receipt_audit_and_external_head()
    {
        using var f = new AuthorityHarness(); var command = await Command(f);
        var domain = await OutpostDomainDigest.ComputeAsync(f.Db, OutpostDomainFormat.V1, default);
        var world = OutpostRestoreEvidence.Encode(await f.Db.World.AsNoTracking().SingleAsync());
        var result = await OutpostAdminActivationTests.Service(f).InitializeAsync(command.Intent.Actor, command, default);
        Assert.False(result.Replay); Assert.True(result.Outcome.NoReplayAuthorization);
        Assert.Equal(OutpostAdminDurableResultKind.AuthorityInitialized, result.Outcome.Kind);
        Assert.Equal(domain, await OutpostDomainDigest.ComputeAsync(f.Db, OutpostDomainFormat.V1, default));
        Assert.Equal(world, OutpostRestoreEvidence.Encode(await f.Db.World.AsNoTracking().SingleAsync()));
        var transition = await f.Db.OutpostTransitions.AsNoTracking().SingleAsync();
        var receipt = await f.Db.OutpostAdminCommands.AsNoTracking().SingleAsync();
        var audit = await f.Db.AdminActions.AsNoTracking().SingleAsync();
        var row = await f.Db.OutpostAuthority.AsNoTracking().SingleAsync();
        Assert.Equal(command.Intent.Allocation.TransitionId, transition.Id);
        Assert.Equal(command.Intent.Allocation.ReceiptId, receipt.Id); Assert.Equal(command.Intent.Allocation.AuditId, audit.Id);
        Assert.Equal(command.Intent.Allocation.At, transition.CreatedAt); Assert.Equal(command.Intent.Allocation.At, audit.At);
        Assert.Equal(command.Intent.Initialize!.Installation, row.InstallationId); Assert.Equal(command.Intent.Initialize.Boundary, row.RestoreBoundary);
        Assert.Equal(command.Intent.Allocation.NextHeadId, result.Head.Id); Assert.Equal(transition.Digest, result.Head.Digest.ToByteArray());
        Assert.Equal((await OutpostDomainDigest.ComputeAsync(f.Db, OutpostDomainFormat.V2, default)).ToByteArray(), transition.Effects);
        var binding = OutpostAdminReceiptEvidence.ReadInitial(transition); Assert.Equal(OutpostDomainFormat.V2, binding.DomainFormat);
        Assert.Equal(command.Intent.Initialize.Schema.ProfileId, binding.Schema.ProfileId);
        Assert.Equal(OutpostAdminIntentCodec.Encode(command.Intent), receipt.Command);
        Assert.Equal(new OutpostStartupResult(OutpostStartupState.Ready), await f.Authority.OpenAsync(default));
        using (var pin = f.MainObservation.TryPin()) Assert.NotNull(pin);
        Assert.False(f.Feature.IsAvailable);
    }

    [Fact]
    public async Task Replay_returns_the_original_initialized_head_without_repeating_rows_even_after_a_later_transition()
    {
        using var f = new AuthorityHarness(); var command = await Command(f); var service = OutpostAdminActivationTests.Service(f);
        var first = await service.InitializeAsync(command.Intent.Actor, command, default);
        await using (var lease = await f.Authority.AcquireAsync([], default))
            await f.Authority.CommitHeldAsync(lease, new(Guid.NewGuid().ToString("D"), command.Intent.Expected.Uninitialized!.World, 0, 0,
                command.Intent.Initialize!.Boundary, "Production", "later-test", first.Head, ByteString.CopyFromUtf8("no-domain-effects")), (_, _) => Task.CompletedTask, default);
        var before = await Cut(f);
        var replay = await service.InitializeAsync(command.Intent.Actor, command, default);
        Assert.True(replay.Replay); Assert.Equal(first.Head, replay.Head);
        Assert.Equal(OutpostRestoreEvidence.Encode(first.Outcome), OutpostRestoreEvidence.Encode(replay.Outcome));
        Assert.Equal(before, await Cut(f)); Assert.Equal(2, await f.Db.OutpostTransitions.CountAsync());
    }

    [Theory]
    [InlineData("BeforePending", false)][InlineData("AfterPending", false)]
    [InlineData("AfterDomainCommit", true)][InlineData("AfterExternalCommit", true)]
    public async Task Only_the_original_initialize_command_reconstructs_or_replays_the_exact_durable_decision(string point, bool committed)
    {
        using var f = new AuthorityHarness(); var command = await Command(f);
        f.Authority.CommitCheckpoint = stage => { if (stage == point) throw new Stop(); };
        await Assert.ThrowsAsync<Stop>(() => OutpostAdminActivationTests.Service(f).InitializeAsync(command.Intent.Actor, command, default));
        f.Authority.CommitCheckpoint = null; f.Db.ChangeTracker.Clear();
        Assert.Equal(committed ? 1 : 0, await f.Db.OutpostAdminCommands.CountAsync());
        Assert.Equal(committed ? 1 : 0, await f.Db.AdminActions.CountAsync());
        if (point == "AfterPending")
        {
            var before = await Cut(f);
            Assert.Equal(OutpostStartupState.Maintenance, (await f.Authority.OpenAsync(default)).State);
            Assert.Equal(before, await Cut(f)); // Normal startup must not clear the unmatched initial Pending.
        }
        var result = await OutpostAdminActivationTests.Service(f).InitializeAsync(command.Intent.Actor, command, default);
        Assert.Equal(committed, result.Replay);
        Assert.Equal(command.Intent.Allocation.NextHeadId, result.Head.Id);
        Assert.Single(await f.Db.OutpostTransitions.AsNoTracking().ToListAsync());
        Assert.Single(await f.Db.OutpostAdminCommands.AsNoTracking().ToListAsync());
        Assert.Single(await f.Db.AdminActions.AsNoTracking().ToListAsync());
        var replay = await OutpostAdminActivationTests.Service(f).InitializeAsync(command.Intent.Actor, command, default);
        Assert.True(replay.Replay); Assert.Equal(result.Head, replay.Head);
        Assert.Equal(new OutpostStartupResult(OutpostStartupState.Ready), await f.Authority.OpenAsync(default));
    }

    [Theory]
    [InlineData("reason")][InlineData("allocation")][InlineData("world-proof")][InlineData("qualification")]
    [InlineData("actor")][InlineData("digest")][InlineData("confirmation")][InlineData("audit-collision")]
    public async Task A_changed_initial_pending_candidate_never_clears_the_fence_or_commits_approximate_evidence(string change)
    {
        using var f = new AuthorityHarness(); var original = await Command(f);
        f.Authority.CommitCheckpoint = point => { if (point == "AfterPending") throw new Stop(); };
        await Assert.ThrowsAsync<Stop>(() => OutpostAdminActivationTests.Service(f).InitializeAsync(original.Intent.Actor, original, default));
        f.Authority.CommitCheckpoint = null; f.Db.ChangeTracker.Clear();
        var intent = OutpostAdminIntentCodec.Freeze(original.Intent);
        if (change == "reason") intent = intent with { Reason = "different reason" };
        if (change == "allocation") intent = intent with { Allocation = intent.Allocation with { ReceiptId = Guid.NewGuid().ToString("D") } };
        if (change == "world-proof") intent.Expected.Uninitialized!.WorldBindingDigest[0] ^= 1;
        if (change == "qualification") intent.Expected.Uninitialized!.QualificationDigest[0] ^= 1;
        if (change == "audit-collision")
        { f.Db.AdminActions.Add(new() { Id = intent.Allocation.AuditId, ActorName = "other", Action = "unrelated", Details = "retained", At = DateTime.UnixEpoch }); await f.Db.SaveChangesAsync(); f.Db.ChangeTracker.Clear(); }
        var digest = OutpostAdminIntentCodec.Digest(intent); if (change == "digest") digest[0] ^= 1;
        var command = new OutpostAdminCommand(intent, digest, change != "confirmation");
        var actor = change == "actor" ? intent.Actor with { Name = "different-admin" } : intent.Actor;
        var before = await Cut(f);
        await Assert.ThrowsAsync<OutpostEvidenceUnavailableException>(() => OutpostAdminActivationTests.Service(f).InitializeAsync(actor, command, default));
        Assert.Equal(before, await Cut(f));
        using var anchorLease = await f.Anchor.AcquireAsync(default);
        Assert.NotNull(f.Anchor.Read(anchorLease)!.Pending); Assert.Empty(await f.Db.OutpostAuthority.ToListAsync());
    }

    [Theory]
    [InlineData("world-proof")][InlineData("qualification")][InlineData("confirmation")][InlineData("digest")][InlineData("actor")]
    [InlineData("audit-collision")][InlineData("orphan-initial-audit")][InlineData("world-change")]
    public async Task Invalid_fresh_initialization_never_creates_a_logical_seed_or_any_receipt(string change)
    {
        using var f = new AuthorityHarness(); var original = await Command(f); var intent = OutpostAdminIntentCodec.Freeze(original.Intent);
        if (change == "world-proof") intent.Expected.Uninitialized!.WorldBindingDigest[0] ^= 1;
        if (change == "qualification") intent.Expected.Uninitialized!.QualificationDigest[0] ^= 1;
        if (change is "audit-collision" or "orphan-initial-audit")
        { f.Db.AdminActions.Add(new() { Id = change == "audit-collision" ? intent.Allocation.AuditId : 123, ActorName = "retained", Action = change == "orphan-initial-audit" ? "outposts.Initialize" : "unrelated", Details = "retained", At = DateTime.UnixEpoch }); await f.Db.SaveChangesAsync(); }
        if (change == "world-change") { var world = await f.Db.World.SingleAsync(); world.Seed += "-changed"; await f.Db.SaveChangesAsync(); }
        f.Db.ChangeTracker.Clear(); var before = await Cut(f);
        var digest = OutpostAdminIntentCodec.Digest(intent); if (change == "digest") digest[0] ^= 1;
        var actor = change == "actor" ? intent.Actor with { Name = "different-admin" } : intent.Actor;
        await Assert.ThrowsAsync<OutpostEvidenceUnavailableException>(() => OutpostAdminActivationTests.Service(f).InitializeAsync(actor, new(intent, digest, change != "confirmation"), default));
        Assert.Equal(before, await Cut(f)); using var held = await f.Anchor.AcquireAsync(default); Assert.Null(f.Anchor.ReadEnvelope(held));
    }

    [Fact]
    public async Task A_cold_keeper_is_prepared_only_by_the_confirmed_mutation_not_by_later_get_fallback()
    {
        using var f = new AuthorityHarness(); var command = await Command(f); await f.MainObservation.DrainAsync(default);
        var result = await OutpostAdminActivationTests.Service(f).InitializeAsync(command.Intent.Actor, command, default);
        Assert.False(result.Replay);
        using (var pin = f.MainObservation.TryPin()) Assert.NotNull(pin);
        Assert.Equal(OutpostAuthorityObservationKind.Committed, (await OutpostAdminActivationTests.Service(f).InspectAsync(Actor.Api, default)).Kind);
    }

    [Fact]
    public async Task A_waiting_initialization_owns_detached_original_command_buffers()
    {
        using var f = new AuthorityHarness(); var command = await Command(f); Task<OutpostAdminCommitResult> pending;
        using (var blocker = await f.Anchor.AcquireAsync(default))
        {
            pending = OutpostAdminActivationTests.Service(f).InitializeAsync(command.Intent.Actor, command, default); Assert.False(pending.IsCompleted);
            command.IntentDigest[0] ^= 1; command.Intent.Expected.Uninitialized!.WorldBindingDigest[0] ^= 1;
            command.Intent.Expected.Uninitialized.QualificationDigest[0] ^= 1; command.Intent.Initialize!.Schema.Digest[0] ^= 1;
        }
        var result = await pending; Assert.False(result.Replay);
        var stored = await f.Db.OutpostAdminCommands.AsNoTracking().SingleAsync(); Assert.NotEqual(command.IntentDigest, stored.CommandDigest);
        Assert.Equal(new OutpostStartupResult(OutpostStartupState.Ready), await f.Authority.OpenAsync(default));
    }

    [Fact]
    public async Task A_changed_command_after_domain_commit_does_not_clear_the_initial_pending()
    {
        using var f = new AuthorityHarness(); var original = await Command(f);
        f.Authority.CommitCheckpoint = point => { if (point == "AfterDomainCommit") throw new Stop(); };
        await Assert.ThrowsAsync<Stop>(() => OutpostAdminActivationTests.Service(f).InitializeAsync(original.Intent.Actor, original, default));
        f.Authority.CommitCheckpoint = null; f.Db.ChangeTracker.Clear(); var before = await Cut(f);
        var changed = original.Intent with { Reason = "not the recorded initialize command" };
        await Assert.ThrowsAsync<OutpostEvidenceUnavailableException>(() => OutpostAdminActivationTests.Service(f).InitializeAsync(changed.Actor,
            new(changed, OutpostAdminIntentCodec.Digest(changed), true), default));
        Assert.Equal(before, await Cut(f)); using var held = await f.Anchor.AcquireAsync(default); Assert.NotNull(f.Anchor.Read(held)!.Pending);
    }

    [Fact]
    public async Task Missing_exact_audit_after_domain_commit_blocks_external_completion_without_repair()
    {
        using var f = new AuthorityHarness(); var command = await Command(f);
        f.Authority.CommitCheckpoint = point => { if (point == "AfterDomainCommit") throw new Stop(); };
        await Assert.ThrowsAsync<Stop>(() => OutpostAdminActivationTests.Service(f).InitializeAsync(command.Intent.Actor, command, default));
        f.Authority.CommitCheckpoint = null; f.Db.ChangeTracker.Clear();
        await f.Db.AdminActions.ExecuteDeleteAsync(); var before = await Cut(f);
        await Assert.ThrowsAsync<OutpostEvidenceUnavailableException>(() => OutpostAdminActivationTests.Service(f).InitializeAsync(command.Intent.Actor, command, default));
        Assert.Equal(before, await Cut(f)); using var held = await f.Anchor.AcquireAsync(default); Assert.NotNull(f.Anchor.Read(held)!.Pending);
    }

    [Fact]
    public async Task A_missing_main_is_refused_before_owner_discovery_without_creating_any_replacement_database()
    {
        using var f = new AuthorityHarness(); var command = await Command(f);
        await f.MainObservation.DrainAsync(default); await f.Db.Database.CloseConnectionAsync();
        File.Move(f.Data.DatabasePath, f.Data.DatabasePath + ".retained-initialize-proof");
        var before = OutpostAdminInspectionTests.Files(f);
        await Assert.ThrowsAsync<OutpostEvidenceUnavailableException>(() => OutpostAdminActivationTests.Service(f).InitializeAsync(command.Intent.Actor, command, default));
        Assert.False(File.Exists(f.Data.DatabasePath)); Assert.Equal(before, OutpostAdminInspectionTests.Files(f));
    }

    [Fact]
    public async Task Initialize_requires_the_authenticated_admin_even_when_a_body_contains_a_valid_admin_command()
    {
        using var f = new AuthorityHarness(); var command = await Command(f); var before = await Cut(f);
        var error = await Assert.ThrowsAsync<global::Grpc.Core.RpcException>(() => OutpostAdminActivationTests.Service(f).InitializeAsync(
            new(1, 1, Rimlink.Server.Storage.Entities.Role.Player, "not-admin"), command, default));
        Assert.Equal(global::Grpc.Core.StatusCode.PermissionDenied, error.StatusCode); Assert.Equal(before, await Cut(f));
    }

    internal static async Task<OutpostAdminCommand> Command(AuthorityHarness f)
    {
        await OutpostAdminUninitializedInspectionTests.Prepare(f);
        var preview = await OutpostAdminActivationTests.Service(f).PreviewInitializeAsync(Actor.Api, Guid.NewGuid().ToString("D"), "initialize exact empty installation", default);
        return Assert.IsType<OutpostAdminCommand>(preview.Command) with { Confirmed = true };
    }
    private static async Task<string> Cut(AuthorityHarness f)
    {
        using var lease = await f.Anchor.AcquireAsync(default); var envelope = f.Anchor.ReadEnvelope(lease);
        return string.Join(":", Convert.ToHexString((await OutpostDomainDigest.ComputeAsync(f.Db, OutpostDomainFormat.V1, default)).Span),
            Convert.ToHexString((await OutpostDomainDigest.ComputeAsync(f.Db, OutpostDomainFormat.V2, default)).Span),
            Convert.ToHexString(SHA256.HashData(OutpostRestoreEvidence.Encode(await f.Db.OutpostAuthority.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync()))),
            Convert.ToHexString(SHA256.HashData(OutpostRestoreEvidence.Encode(await f.Db.OutpostTransitions.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync()))),
            Convert.ToHexString(SHA256.HashData(OutpostRestoreEvidence.Encode(await f.Db.AdminActions.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync()))),
            envelope is null ? "NULL" : Convert.ToHexString(AnchorEnvelopeCodec.Encode(envelope)));
    }
    private sealed class Stop : Exception;
}
