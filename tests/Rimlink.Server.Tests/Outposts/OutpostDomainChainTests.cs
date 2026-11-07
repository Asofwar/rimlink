using System.Security.Cryptography;
using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Storage.Entities;
using OutpostAuthority = Rimlink.Server.Application.Outposts.OutpostAuthority;
using Xunit;

namespace Rimlink.Server.Tests.Outposts;

public sealed class OutpostDomainChainTests
{
    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task Verified_initial_binding_selects_exactly_one_digest_format(bool versionTwo)
    {
        using var f = new AuthorityHarness();
        await Legacy(f);
        if (versionTwo)
        {
            // A second independent empty database holds a genuinely new versioned initialization.
            using var fresh = new AuthorityHarness();
            await NewInitialization(fresh);
            await Verify(fresh, OutpostDomainFormat.V2);
            return;
        }
        await Verify(f, OutpostDomainFormat.V1);
    }

    [Fact]
    public async Task New_initial_binding_explicitly_records_format_and_supported_schema()
    {
        using var f = new AuthorityHarness();
        await NewInitialization(f);
        var initial = await f.Db.OutpostTransitions.AsNoTracking().SingleAsync();
        using var json = System.Text.Json.JsonDocument.Parse(initial.Intent);
        Assert.Equal(2, json.RootElement.GetProperty("domainFormat").GetInt32());
        var schema = await OutpostDomainSchema.ValidateAsync(f.Db, OutpostSchemaContext.CurrentRuntime, default);
        Assert.Equal(schema.ProfileId, json.RootElement.GetProperty("schema").GetProperty("profileId").GetString());
    }

    [Fact]
    public async Task Explicit_activation_keeps_the_original_v1_prefix_and_selects_v2_once()
    {
        using var f = new AuthorityHarness();
        await Legacy(f);
        var original = await f.Db.OutpostTransitions.AsNoTracking().SingleAsync();
        byte[] originalBytes = OutpostRestoreEvidence.Encode(original);
        await Activate(f);
        await Verify(f, OutpostDomainFormat.V2);
        Assert.Equal(originalBytes, OutpostRestoreEvidence.Encode(await f.Db.OutpostTransitions.AsNoTracking().SingleAsync(t => t.Id == original.Id)));
    }

    [Theory]
    [InlineData("old-domain")][InlineData("old-head-digest")][InlineData("wrong-format")]
    [InlineData("repeated-activation")][InlineData("missing-receipt")][InlineData("missing-audit")]
    [InlineData("all-receipts-missing")][InlineData("unrelated-admin-row")][InlineData("unbound-format-transition")]
    [InlineData("ordinary-format-claim")][InlineData("tip-v1-effects")]
    public async Task Changed_or_unbound_format_cannot_select_a_more_convenient_digest(string change)
    {
        using var f = new AuthorityHarness();
        await Legacy(f);
        await Activate(f, change);
        var row = await f.Db.OutpostAuthority.AsNoTracking().SingleAsync();
        var before = await OutpostDomainDigest.ComputeAsync(f.Db, OutpostDomainFormat.V2, default);
        await f.Db.Database.ExecuteSqlRawAsync("PRAGMA query_only=ON");
        await Assert.ThrowsAsync<OutpostEvidenceUnavailableException>(() => OutpostAuthority.ReadStoredChainAsync(f.Db, f.Files,
            row, new(row.HeadId, ByteString.CopyFrom(row.HeadDigest)), currentRuntime: true, default));
        Assert.Equal(before, await OutpostDomainDigest.ComputeAsync(f.Db, OutpostDomainFormat.V2, default));
    }

    [Fact]
    public async Task Missing_or_extra_actual_schema_refuses_even_with_a_valid_v1_digest()
    {
        using var f = new AuthorityHarness(); await Legacy(f);
        await f.Db.Database.ExecuteSqlRawAsync("CREATE TABLE sqlitex_unrecognized(value INTEGER)");
        var row = await f.Db.OutpostAuthority.AsNoTracking().SingleAsync();
        await Assert.ThrowsAsync<OutpostEvidenceUnavailableException>(() => OutpostAuthority.ReadStoredChainAsync(f.Db, f.Files,
            row, new(row.HeadId, ByteString.CopyFrom(row.HeadDigest)), true, default));
    }

    [Fact]
    public async Task Runtime_writer_uses_the_verified_v2_format_without_hash_fallback()
    {
        using var f = new AuthorityHarness(); await NewInitialization(f);
        var before = await f.Db.OutpostAuthority.AsNoTracking().SingleAsync();
        f.Anchor.Provision();
        using (var anchor = await f.Anchor.AcquireAsync(default))
            f.Anchor.WriteDurable(anchor, new(1, before.InstallationId, before.WorldId, before.RestoreBoundary,
                new(before.HeadId, ByteString.CopyFrom(before.HeadDigest))), default);
        Assert.Equal(new OutpostStartupResult(OutpostStartupState.Ready), await f.Authority.OpenAsync(default));
        await using (var lease = await f.Authority.AcquireAsync([], default))
            await f.Authority.CommitHeldAsync(lease, new(Guid.NewGuid().ToString("D"), before.WorldId, 0, 0,
                before.RestoreBoundary, "Production", "chain-fixture", new(before.HeadId, ByteString.CopyFrom(before.HeadDigest)),
                ByteString.CopyFromUtf8("no-domain-change")), (_, _) => Task.CompletedTask, default);
        var tip = await f.Db.OutpostTransitions.AsNoTracking().SingleAsync(t => t.Id != before.TransitionId);
        Assert.Equal((await OutpostDomainDigest.ComputeAsync(f.Db, OutpostDomainFormat.V2, default)).ToByteArray(), tip.Effects);
        Assert.Equal(new OutpostStartupResult(OutpostStartupState.Ready), await f.Authority.OpenAsync(default));
        Assert.False(f.Feature.IsAvailable);
    }

    [Fact]
    public async Task Historical_v1_cut_does_not_require_or_create_the_new_admin_table()
    {
        using var f = new AuthorityHarness(); await Legacy(f);
        await f.Db.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>().MigrateAsync("20261104093958_OutpostOrdinaryReturns");
        var row = await f.Db.OutpostAuthority.AsNoTracking().SingleAsync();
        await f.Db.Database.ExecuteSqlRawAsync("PRAGMA query_only=ON");
        var history = await OutpostAuthority.ReadStoredChainAsync(f.Db, f.Files, row,
            new(row.HeadId, ByteString.CopyFrom(row.HeadDigest)), currentRuntime: false, default);
        Assert.Equal(OutpostDomainFormat.V1, history.Format); Assert.False(history.Schema.IsCurrent);
        await Assert.ThrowsAsync<OutpostEvidenceUnavailableException>(() => OutpostAuthority.ReadStoredChainAsync(f.Db, f.Files, row,
            new(row.HeadId, ByteString.CopyFrom(row.HeadDigest)), currentRuntime: true, default));
    }

    private static async Task Verify(AuthorityHarness f, OutpostDomainFormat expected)
    {
        f.Db.ChangeTracker.Clear();
        var row = await f.Db.OutpostAuthority.AsNoTracking().SingleAsync();
        var before = await OutpostDomainDigest.ComputeAsync(f.Db, expected, default);
        await f.Db.Database.ExecuteSqlRawAsync("PRAGMA query_only=ON");
        var observation = await OutpostAuthority.ReadStoredChainAsync(f.Db, f.Files, row,
            new(row.HeadId, ByteString.CopyFrom(row.HeadDigest)), true, default);
        Assert.Equal(expected, observation.Format);
        Assert.True(observation.Schema.IsCurrent);
        Assert.Equal(before, await OutpostDomainDigest.ComputeAsync(f.Db, expected, default));
        Assert.False(f.Db.ChangeTracker.HasChanges());
    }

    private static async Task Legacy(AuthorityHarness f)
    {
        f.Anchor.Provision(); f.Db.World.Add(new() { InfluenceInstanceId = "world-1" }); await f.Db.SaveChangesAsync();
        Assert.Equal(OutpostStartupState.Ready, (await f.Authority.InitializeAsync(new("world-1", "chain-fixture", true), default)).State);
        f.Db.ChangeTracker.Clear();
    }

    private static async Task NewInitialization(AuthorityHarness f)
    {
        f.Db.World.Add(new() { InfluenceInstanceId = "world-1" }); await f.Db.SaveChangesAsync();
        var schema = await OutpostDomainSchema.ValidateAsync(f.Db, OutpostSchemaContext.CurrentRuntime, default);
        var binding = new OutpostSchemaBinding(schema.ProfileId, schema.Digest.ToByteArray());
        var command = OutpostAdminIntentCodecTests.Example(OutpostAdminActionKind.Initialize);
        command = command with { Initialize = command.Initialize! with { Schema = binding },
            Expected = command.Expected with { Uninitialized = command.Expected.Uninitialized! with { Schema = binding } } };
        var result = new OutpostAdminDurableResult(1, command.Allocation.ReceiptId, OutpostAdminDurableResultKind.AuthorityInitialized, null, null, [], true);
        var (receipt, audit) = OutpostAdminReceiptEvidence.Create(command, result);
        f.Db.AddRange(receipt, audit); await f.Db.SaveChangesAsync();
        var transition = Transition(command, receipt, new(Guid.Empty.ToString("D"), ByteString.CopyFrom(SHA256.HashData("rimlink-outpost-genesis-v1"u8))));
        f.Db.OutpostAuthority.Add(new() { Id = 1, FormatVersion = 1, Phase = "Committed", InstallationId = command.Initialize.Installation,
            RestoreBoundary = command.Initialize.Boundary, WorldId = "world-1", HeadId = transition.NextHeadId,
            HeadDigest = new byte[32], TransitionId = transition.Id });
        await SaveTip(f, transition, OutpostDomainFormat.V2);
    }

    private static async Task Activate(AuthorityHarness f, string? change = null)
    {
        var row = await f.Db.OutpostAuthority.AsNoTracking().SingleAsync();
        var schema = await OutpostDomainSchema.ValidateAsync(f.Db, OutpostSchemaContext.CurrentRuntime, default);
        var binding = new OutpostSchemaBinding(schema.ProfileId, schema.Digest.ToByteArray());
        var command = OutpostAdminIntentCodecTests.Example(OutpostAdminActionKind.ActivateDomainFormat);
        command = command with { RequestId = Guid.NewGuid().ToString("D"), Allocation = command.Allocation with
        { ReceiptId = Guid.NewGuid().ToString("D"), TransitionId = Guid.NewGuid().ToString("D"), NextHeadId = Guid.NewGuid().ToString("D"),
            AuditId = 101 + await f.Db.OutpostAdminCommands.CountAsync() } };
        command = command with { Expected = new(OutpostAuthorityObservationKind.Committed, Committed:
            new(row.InstallationId, row.WorldId, row.RestoreBoundary, row.HeadId, (byte[])row.HeadDigest.Clone(), row.TransitionId ?? throw new InvalidOperationException("fixture-transition-missing"), OutpostDomainFormat.V1, binding)),
            Activation = command.Activation! with { PreviousDomainDigest = (await f.Db.OutpostTransitions.AsNoTracking().SingleAsync(t => t.Id == row.TransitionId)).Effects,
                SourceSchema = binding, TargetSchema = binding } };
        if (change == "old-domain") command.Activation!.PreviousDomainDigest[0] ^= 1;
        if (change == "old-head-digest") command.Expected.Committed!.HeadDigest[0] ^= 1;
        var (receipt, audit) = OutpostAdminReceiptEvidence.Create(command,
            new(1, command.Allocation.ReceiptId, OutpostAdminDurableResultKind.DomainFormatActivated, null, null, [], true));
        if (change is not ("missing-receipt" or "all-receipts-missing")) f.Db.Add(receipt);
        if (change is not ("missing-audit" or "all-receipts-missing")) f.Db.Add(audit);
        await f.Db.SaveChangesAsync();
        var transition = Transition(command, receipt, new(row.HeadId, ByteString.CopyFrom(row.HeadDigest)));
        if (change == "unbound-format-transition") transition.Intent = "{}"u8.ToArray();
        if (change == "ordinary-format-claim") transition.Kind = "Production";
        if (change == "wrong-format") transition.Intent = OutpostRestoreEvidence.Encode(new { version = 3, commandId = receipt.Id, commandDigest = receipt.CommandDigest });
        await SaveTip(f, transition, change == "tip-v1-effects" ? OutpostDomainFormat.V1 : OutpostDomainFormat.V2);
        if (change == "unrelated-admin-row")
        {
            var orphan = new OutpostAdminCommandRecord { Id = Guid.NewGuid().ToString("D"), ActorKey = receipt.ActorKey, Action = receipt.Action,
                RequestId = Guid.NewGuid().ToString("D"), Reason = receipt.Reason, Command = receipt.Command, CommandDigest = receipt.CommandDigest,
                TransitionId = Guid.NewGuid().ToString("D"), NextHeadId = Guid.NewGuid().ToString("D"), Result = receipt.Result,
                AuditId = receipt.AuditId + 1, AuditProjection = receipt.AuditProjection, CreatedAt = receipt.CreatedAt };
            f.Db.Add(orphan); await f.Db.SaveChangesAsync();
        }
        if (change == "repeated-activation")
        {
            // A second genuine, internally self-consistent command cannot activate v2 a second time.
            await Activate(f);
        }
    }

    private static OutpostTransition Transition(OutpostAdminIntent command, OutpostAdminCommandRecord receipt, AuthorityHead previous) => new()
    {
        Id = command.Allocation.TransitionId, OldHeadId = previous.Id, NextHeadId = command.Allocation.NextHeadId,
        PredecessorId = command.Expected.Committed?.TransitionId, Kind = OutpostAdminReceiptEvidence.TransitionKind(command.Action),
        Actor = receipt.ActorKey, WorldId = command.Expected.Committed?.World ?? command.Expected.Uninitialized!.World,
        RestoreBoundary = command.Expected.Committed?.Boundary ?? command.Initialize!.Boundary,
        CreatedAt = command.Allocation.At, Intent = OutpostAdminReceiptEvidence.Binding(receipt), Effects = new byte[32], Digest = new byte[32]
    };

    private static async Task SaveTip(AuthorityHarness f, OutpostTransition transition, OutpostDomainFormat format)
    {
        var row = f.Db.OutpostAuthority.Local.SingleOrDefault() ?? await f.Db.OutpostAuthority.SingleAsync();
        var previous = new AuthorityHead(transition.OldHeadId, transition.PredecessorId is null
            ? ByteString.CopyFrom(SHA256.HashData("rimlink-outpost-genesis-v1"u8))
            : ByteString.CopyFrom((await f.Db.OutpostTransitions.AsNoTracking().SingleAsync(t => t.Id == transition.PredecessorId)).Digest));
        transition.Effects = (await OutpostDomainDigest.ComputeAsync(f.Db, format, default)).ToByteArray();
        transition.Digest = OutpostTransitionDigest.Compute(previous, transition).ToByteArray();
        f.Db.Add(transition); row.HeadId = transition.NextHeadId; row.HeadDigest = transition.Digest; row.TransitionId = transition.Id;
        await f.Db.SaveChangesAsync(); f.Db.ChangeTracker.Clear();
    }
}
