using System.Security.Cryptography;
using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Rimlink.Server.Application.Admin;
using Rimlink.Server.Application.Feed;
using Rimlink.Server.Application.Live;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Hosting;
using Rimlink.Server.Storage.Entities;
using Xunit;
using Role = Rimlink.Server.Storage.Entities.Role;

namespace Rimlink.Server.Tests.Outposts;

public sealed class OutpostAdminActivationTests
{
    [Fact]
    public async Task Explicit_activation_is_one_protected_transition_without_any_v1_domain_effect()
    {
        using var f = new AuthorityHarness(); var command = await Prepare(f);
        var before = await Cut(f); var original = await f.Db.OutpostTransitions.AsNoTracking().SingleAsync();
        byte[] originalBytes = OutpostRestoreEvidence.Encode(original);
        var service = Service(f); var reply = await service.ActivateDomainFormatAsync(command.Intent.Actor, command, default);
        Assert.False(reply.Replay); Assert.True(reply.Outcome.NoReplayAuthorization);
        Assert.Equal(OutpostAdminDurableResultKind.DomainFormatActivated, reply.Outcome.Kind);
        Assert.Equal(before.DomainBytes, (await OutpostDomainDigest.ComputeAsync(f.Db, OutpostDomainFormat.V1, default)).ToByteArray());
        Assert.Equal(2, await f.Db.OutpostTransitions.CountAsync());
        Assert.Equal(originalBytes, OutpostRestoreEvidence.Encode(await f.Db.OutpostTransitions.AsNoTracking().SingleAsync(t => t.Id == original.Id)));
        var receipt = await f.Db.OutpostAdminCommands.AsNoTracking().SingleAsync();
        var transition = await f.Db.OutpostTransitions.AsNoTracking().SingleAsync(t => t.Id == receipt.TransitionId);
        Assert.Equal(command.Intent.Allocation.NextHeadId, reply.Head.Id);
        Assert.Equal(transition.Digest, reply.Head.Digest.ToByteArray());
        Assert.Equal(command.Intent.Allocation.At, transition.CreatedAt);
        Assert.Equal(command.Intent.Allocation.AuditId, (await f.Db.AdminActions.SingleAsync()).Id);
        Assert.Equal((await OutpostDomainDigest.ComputeAsync(f.Db, OutpostDomainFormat.V2, default)).ToByteArray(), transition.Effects);
        Assert.Equal(new OutpostStartupResult(OutpostStartupState.Ready), await f.Authority.OpenAsync(default));
        Assert.False(f.Feature.IsAvailable);
    }

    [Fact]
    public async Task Exact_actor_action_request_replay_keeps_the_original_command_head_and_audit()
    {
        using var f = new AuthorityHarness(); var command = await Prepare(f); var service = Service(f);
        var first = await service.ActivateDomainFormatAsync(command.Intent.Actor, command, default);
        var cut = await Cut(f);
        var replay = await service.ActivateDomainFormatAsync(command.Intent.Actor, command, default);
        Assert.True(replay.Replay); Assert.Equal(first.Head, replay.Head); Assert.Equal(OutpostRestoreEvidence.Encode(first.Outcome), OutpostRestoreEvidence.Encode(replay.Outcome));
        Assert.Equal(cut, await Cut(f));
        var changed = command.Intent with { Reason = "not the recorded reason" };
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => service.ActivateDomainFormatAsync(changed.Actor,
            new(changed, OutpostAdminIntentCodec.Digest(changed), true), default));
        Assert.Equal(cut, await Cut(f));
    }

    [Theory]
    [InlineData("head-id")][InlineData("head-digest")][InlineData("transition")][InlineData("installation")]
    [InlineData("boundary")][InlineData("world")][InlineData("old-domain")][InlineData("evidence")]
    [InlineData("digest")][InlineData("confirmation")][InlineData("actor")][InlineData("audit-collision")]
    public async Task Stale_or_changed_activation_has_no_domain_anchor_or_audit_effect(string change)
    {
        using var f = new AuthorityHarness(); var command = await Prepare(f); var intent = command.Intent;
        var expected = intent.Expected.Committed!; var activation = intent.Activation!;
        if (change == "head-id") expected = expected with { HeadId = Guid.NewGuid().ToString("D") };
        if (change == "head-digest") expected.HeadDigest[0] ^= 1;
        if (change == "transition") expected = expected with { TransitionId = Guid.NewGuid().ToString("D") };
        if (change == "installation") expected = expected with { Installation = Guid.NewGuid().ToString("D") };
        if (change == "boundary") expected = expected with { Boundary = Guid.NewGuid().ToString("D") };
        if (change == "world") expected = expected with { World = "foreign-world" };
        if (change == "old-domain") activation.PreviousDomainDigest[0] ^= 1;
        if (change == "evidence") intent.Evidence[0].Digest![0] ^= 1;
        intent = intent with { Expected = intent.Expected with { Committed = expected }, Activation = activation };
        var digest = OutpostAdminIntentCodec.Digest(intent);
        if (change == "digest") digest[0] ^= 1;
        command = new(intent, digest, change != "confirmation");
        var actor = change == "actor" ? intent.Actor with { Name = "other-authenticated-admin" } : intent.Actor;
        if (change == "audit-collision")
        { f.Db.AdminActions.Add(new() { Id = intent.Allocation.AuditId, Action = "unrelated", Details = "untouched", At = DateTime.UnixEpoch }); await f.Db.SaveChangesAsync(); }
        f.Db.ChangeTracker.Clear(); var cut = await Cut(f);
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => Service(f).ActivateDomainFormatAsync(actor, command, default));
        Assert.Equal(cut, await Cut(f));
        Assert.Equal(new OutpostStartupResult(OutpostStartupState.Ready), await f.Authority.OpenAsync(default));
    }

    [Theory]
    [InlineData("BeforePending", false)][InlineData("AfterPending", false)]
    [InlineData("AfterDomainCommit", true)][InlineData("AfterExternalCommit", true)]
    public async Task Activation_interruption_recovers_exact_old_or_new_format(string checkpoint, bool committed)
    {
        using var f = new AuthorityHarness(); var command = await Prepare(f);
        f.Authority.CommitCheckpoint = point => { if (point == checkpoint) throw new Stop(); };
        await Assert.ThrowsAsync<Stop>(() => Service(f).ActivateDomainFormatAsync(command.Intent.Actor, command, default));
        f.Authority.CommitCheckpoint = null; f.Db.ChangeTracker.Clear();
        Assert.Equal(new OutpostStartupResult(OutpostStartupState.Ready), await f.Authority.OpenAsync(default));
        var row = await f.Db.OutpostAuthority.AsNoTracking().SingleAsync();
        var observed = await Rimlink.Server.Application.Outposts.OutpostAuthority.ReadStoredChainAsync(f.Db, f.Files, row,
            new(row.HeadId, ByteString.CopyFrom(row.HeadDigest)), true, default);
        Assert.Equal(committed ? OutpostDomainFormat.V2 : OutpostDomainFormat.V1, observed.Format);
        Assert.Equal(committed ? 1 : 0, await f.Db.OutpostAdminCommands.CountAsync());
        Assert.Equal(committed ? 1 : 0, await f.Db.AdminActions.CountAsync());
        var result = await Service(f).ActivateDomainFormatAsync(command.Intent.Actor, command, default);
        Assert.Equal(committed, result.Replay);
        Assert.Single(await f.Db.OutpostAdminCommands.AsNoTracking().ToListAsync());
        Assert.Equal(2, await f.Db.OutpostTransitions.CountAsync());
    }

    [Fact]
    public async Task Caller_mutation_while_waiting_for_authority_cannot_change_the_frozen_command()
    {
        using var f = new AuthorityHarness(); var command = await Prepare(f);
        Task<OutpostAdminCommitResult> pending;
        using (var blocker = await f.Anchor.AcquireAsync(default))
        {
            pending = Service(f).ActivateDomainFormatAsync(command.Intent.Actor, command, default);
            Assert.False(pending.IsCompleted);
            command.IntentDigest[0] ^= 1; command.Intent.Expected.Committed!.HeadDigest[0] ^= 1;
            command.Intent.Activation!.PreviousDomainDigest[0] ^= 1; command.Intent.Evidence[0].Digest![0] ^= 1;
        }
        var result = await pending;
        Assert.False(result.Replay);
        var stored = OutpostAdminIntentCodec.Decode((await f.Db.OutpostAdminCommands.AsNoTracking().SingleAsync()).Command);
        Assert.NotEqual(command.IntentDigest, OutpostAdminIntentCodec.Digest(stored));
        Assert.Equal(new OutpostStartupResult(OutpostStartupState.Ready), await f.Authority.OpenAsync(default));
    }

    [Fact]
    public async Task Activation_does_not_settle_or_mutate_real_stock_fraction_custody_or_original_bytes()
    {
        using var h = await MutationHarness.Create(); var f = h.F.H.H;
        h.F.Time.Advance(TimeSpan.FromMinutes(31.5));
        await h.Service.StateAsync(1, new() { Scope = h.Scope.Clone() }, default);
        var before = await Cut(f);
        var stock = await h.Row(); Assert.Equal(10, stock.Stock);
        var files = Directory.EnumerateFiles(f.Data.SavesRoot, "*", SearchOption.AllDirectories)
            .ToDictionary(path => path, path => SHA256.HashData(File.ReadAllBytes(path)));
        var command = await CommandForCurrent(f);
        h.F.Time.Advance(TimeSpan.FromDays(2));
        await Service(f).ActivateDomainFormatAsync(command.Intent.Actor, command, default);
        Assert.Equal(before.DomainBytes, (await OutpostDomainDigest.ComputeAsync(f.Db, OutpostDomainFormat.V1, default)).ToByteArray());
        Assert.Equal(OutpostRestoreEvidence.Encode(stock), OutpostRestoreEvidence.Encode(await h.Row()));
        foreach (var pair in files) Assert.Equal(pair.Value, SHA256.HashData(File.ReadAllBytes(pair.Key)));
        Assert.Equal(new OutpostStartupResult(OutpostStartupState.Ready), await f.Authority.OpenAsync(default));
    }

    internal static async Task<OutpostAdminCommand> Prepare(AuthorityHarness f)
    {
        f.Anchor.Provision(); f.Db.World.Add(new() { InfluenceInstanceId = "world-1" }); await f.Db.SaveChangesAsync();
        Assert.Equal(OutpostStartupState.Ready, (await f.Authority.InitializeAsync(new("world-1", "legacy-fixture", true), default)).State);
        return await CommandForCurrent(f);
    }

    internal static async Task<OutpostAdminCommand> CommandForCurrent(AuthorityHarness f)
    {
        var row = await f.Db.OutpostAuthority.AsNoTracking().SingleAsync();
        var tip = await f.Db.OutpostTransitions.AsNoTracking().SingleAsync(t => t.Id == row.TransitionId);
        var schema = await OutpostDomainSchema.ValidateAsync(f.Db, OutpostSchemaContext.CurrentRuntime, default);
        var binding = new OutpostSchemaBinding(schema.ProfileId, schema.Digest.ToByteArray());
        var intent = OutpostAdminIntentCodecTests.Example(OutpostAdminActionKind.ActivateDomainFormat);
        intent = intent with { Actor = Actor.Api, Evidence = [new(OutpostAdminEvidenceKind.Transition, tip.Id, tip.Digest)],
            Expected = new(OutpostAuthorityObservationKind.Committed, Committed: new(row.InstallationId, row.WorldId, row.RestoreBoundary,
                row.HeadId, row.HeadDigest, row.TransitionId!, OutpostDomainFormat.V1, binding)),
            Activation = new(OutpostDomainFormat.V1, OutpostDomainFormat.V2, tip.Effects, binding, binding, OutpostDomainSchema.AdminMigrationId) };
        f.Db.ChangeTracker.Clear(); return new(intent, OutpostAdminIntentCodec.Digest(intent), true);
    }

    internal static OutpostAdminService Service(AuthorityHarness f)
    {
        var feed = new AdminFeed(Options.Create(new AdminOptions()), TimeProvider.System, NullLogger<AdminFeed>.Instance);
        return new(f.Db, f.Authority, new LiveStateLock(), new AdminAudit(f.Db, feed, NullLogger<AdminAudit>.Instance), f.MainObservation,
            new OutpostManifestService(f.Db, f.Authority, new OutpostClock(TimeProvider.System), f.Files));
    }
    private sealed class Stop : Exception;
    private sealed record State(string Domain, string Transitions, string Commands, string Audits, string Anchor)
    { public byte[] DomainBytes => Convert.FromHexString(Domain); }
    private static async Task<State> Cut(AuthorityHarness f)
    {
        using var lease = await f.Anchor.AcquireAsync(default);
        return new(Convert.ToHexString((await OutpostDomainDigest.ComputeAsync(f.Db, OutpostDomainFormat.V1, default)).Span),
            Convert.ToHexString(SHA256.HashData(OutpostRestoreEvidence.Encode(await f.Db.OutpostTransitions.AsNoTracking().OrderBy(t => t.Id).ToArrayAsync()))),
            Convert.ToHexString(SHA256.HashData(OutpostRestoreEvidence.Encode(await f.Db.OutpostAdminCommands.AsNoTracking().OrderBy(t => t.Id).ToArrayAsync()))),
            Convert.ToHexString(SHA256.HashData(OutpostRestoreEvidence.Encode(await f.Db.AdminActions.AsNoTracking().OrderBy(t => t.Id).ToArrayAsync()))),
            Convert.ToHexString(AnchorEnvelopeCodec.Encode(f.Anchor.ReadEnvelope(lease)!)));
    }
}
