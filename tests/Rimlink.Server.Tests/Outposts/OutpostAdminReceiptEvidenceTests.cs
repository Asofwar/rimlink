using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Storage.Entities;
using Xunit;

namespace Rimlink.Server.Tests.Outposts;

public sealed class OutpostAdminReceiptEvidenceTests
{
    [Theory]
    [InlineData(OutpostAdminActionKind.Initialize)]
    [InlineData(OutpostAdminActionKind.ActivateDomainFormat)]
    [InlineData(OutpostAdminActionKind.ConfirmManifest)]
    [InlineData(OutpostAdminActionKind.Resolve)]
    public async Task Command_audit_and_transition_links_are_verified_without_writes(OutpostAdminActionKind action)
    {
        using var f = new AuthorityHarness();
        var (receipt, audit, transition) = await Rows(f, action);
        f.Db.AddRange(receipt, audit, transition); await f.Db.SaveChangesAsync(); f.Db.ChangeTracker.Clear();
        var before = await OutpostDomainDigest.ComputeAsync(f.Db, OutpostDomainFormat.V2, default);
        var schema = await OutpostDomainSchema.ValidateAsync(f.Db, OutpostSchemaContext.CurrentRuntime, default);
        await f.Db.Database.ExecuteSqlRawAsync("PRAGMA query_only=ON");
        await OutpostAdminReceiptEvidence.ValidateAsync(f.Db, await f.Db.OutpostTransitions.AsNoTracking().ToListAsync(), schema, default);
        Assert.Equal(before, await OutpostDomainDigest.ComputeAsync(f.Db, OutpostDomainFormat.V2, default));
        Assert.False(f.Db.ChangeTracker.HasChanges());
    }

    [Theory]
    [InlineData("missing-receipt")][InlineData("missing-audit")][InlineData("missing-transition")]
    [InlineData("actor")][InlineData("action")][InlineData("request")][InlineData("reason")]
    [InlineData("command")][InlineData("command-digest")][InlineData("result")][InlineData("result-kind")]
    [InlineData("audit-fields")][InlineData("audit-projection")][InlineData("audit-time")]
    [InlineData("transition-actor")][InlineData("transition-head")][InlineData("transition-binding")]
    [InlineData("transition-time")][InlineData("transition-kind")][InlineData("extra-receipt")]
    public async Task Missing_or_changed_link_cannot_be_treated_as_legacy_or_success(string change)
    {
        using var f = new AuthorityHarness();
        var (receipt, audit, transition) = await Rows(f, OutpostAdminActionKind.ActivateDomainFormat);
        switch (change)
        {
            case "actor": receipt.ActorKey = new string('f', 64); break;
            case "action": receipt.Action = "Resolve"; break;
            case "request": receipt.RequestId = Guid.NewGuid().ToString("D"); break;
            case "reason": receipt.Reason += " changed"; break;
            case "command": receipt.Command[^1] ^= 1; break;
            case "command-digest": receipt.CommandDigest[0] ^= 1; break;
            case "result": receipt.Result = "{\"version\":1}"u8.ToArray(); break;
            case "result-kind":
                var wrong = new OutpostAdminDurableResult(1, receipt.Id, OutpostAdminDurableResultKind.AuthorityInitialized, null, null, [], true);
                receipt.Result = OutpostRestoreEvidence.Encode(wrong); break;
            case "audit-fields": audit.Details += " changed"; break;
            case "audit-projection": receipt.AuditProjection = "[]"u8.ToArray(); break;
            case "audit-time": audit.At = audit.At.AddTicks(1); break;
            case "transition-actor": transition.Actor = "other"; break;
            case "transition-head": transition.NextHeadId = Guid.NewGuid().ToString("D"); break;
            case "transition-binding": transition.Intent = OutpostRestoreEvidence.Encode(new OutpostAdminTransitionBinding(2, receipt.Id, new byte[32])); break;
            case "transition-time": transition.CreatedAt = transition.CreatedAt.AddTicks(1); break;
            case "transition-kind": transition.Kind = "Production"; break;
        }
        if (change != "missing-receipt") f.Db.Add(receipt);
        if (change != "missing-audit") f.Db.Add(audit);
        if (change == "extra-receipt")
        {
            var (extra, extraAudit, _) = await Rows(f, OutpostAdminActionKind.ConfirmManifest);
            extra.Id = Guid.NewGuid().ToString("D"); extra.TransitionId = Guid.NewGuid().ToString("D");
            extra.NextHeadId = Guid.NewGuid().ToString("D"); extra.AuditId = 102; extraAudit.Id = 102;
            f.Db.AddRange(extra, extraAudit);
        }
        f.Db.Add(transition); await f.Db.SaveChangesAsync(); f.Db.ChangeTracker.Clear();
        var schema = await OutpostDomainSchema.ValidateAsync(f.Db, OutpostSchemaContext.CurrentRuntime, default);
        var retained = await f.Db.OutpostTransitions.AsNoTracking().ToListAsync();
        if (change == "missing-transition") retained.RemoveAll(t => t.Id == transition.Id);
        await Assert.ThrowsAsync<OutpostEvidenceUnavailableException>(() => OutpostAdminReceiptEvidence.ValidateAsync(f.Db, retained, schema, default));
    }

    private static async Task<(OutpostAdminCommandRecord, AdminAction, OutpostTransition)> Rows(AuthorityHarness f, OutpostAdminActionKind action)
    {
        var intent = OutpostAdminIntentCodecTests.Example(action);
        if (action != OutpostAdminActionKind.Initialize)
        {
            if (!await f.Db.OutpostTransitions.AnyAsync())
            {
                f.Anchor.Provision();
                f.Db.World.Add(new Rimlink.Server.Storage.Entities.World { InfluenceInstanceId = "world-1" });
                await f.Db.SaveChangesAsync();
                Assert.Equal(OutpostStartupState.Ready, (await f.Authority.InitializeAsync(new("world-1", "receipt-fixture", true), default)).State);
            }
            var head = await f.Db.OutpostAuthority.AsNoTracking().SingleAsync();
            intent = intent with { Expected = intent.Expected with { Committed = intent.Expected.Committed! with
            { Installation = head.InstallationId, World = head.WorldId, Boundary = head.RestoreBoundary,
                HeadId = head.HeadId, HeadDigest = (byte[])head.HeadDigest.Clone(), TransitionId = head.TransitionId ?? throw new InvalidOperationException("fixture-transition-missing") } } };
            if (intent.Resolution is { } resolution)
            {
                var scope = resolution.Scope.Clone(); scope.RestoreBoundary = head.RestoreBoundary;
                var proof = resolution.CanonicalSnapshot!.Clone(); proof.Scope = scope.Clone();
                intent = intent with { Resolution = resolution with { Scope = scope, CanonicalSnapshot = proof } };
            }
        }
        var kind = action switch
        {
            OutpostAdminActionKind.Initialize => OutpostAdminDurableResultKind.AuthorityInitialized,
            OutpostAdminActionKind.ActivateDomainFormat => OutpostAdminDurableResultKind.DomainFormatActivated,
            OutpostAdminActionKind.ConfirmManifest => OutpostAdminDurableResultKind.ManifestConfirmed,
            _ => OutpostAdminDurableResultKind.OrdinaryOutcome
        };
        var references = action == OutpostAdminActionKind.ConfirmManifest
            ? new[] { new OutpostAdminEvidenceReference(OutpostAdminEvidenceKind.Manifest, intent.Manifest!.ManifestId, intent.Manifest.Digest) }
            : Array.Empty<OutpostAdminEvidenceReference>();
        var result = new OutpostAdminDurableResult(1, intent.Allocation.ReceiptId, kind, null,
            action == OutpostAdminActionKind.Resolve ? Rimlink.Protocol.V1.OrdinaryOperationStatus.ReturnAccepted : null, references, true);
        var (receipt, audit) = OutpostAdminReceiptEvidence.Create(intent, result);
        var expected = intent.Expected.Committed;
        var transition = new OutpostTransition
        {
            Id = intent.Allocation.TransitionId, WorldId = expected?.World ?? intent.Expected.Uninitialized!.World,
            RestoreBoundary = expected?.Boundary ?? intent.Initialize!.Boundary,
            Actor = receipt.ActorKey, Kind = OutpostAdminReceiptEvidence.TransitionKind(action),
            PlayerId = intent.Resolution?.Scope.OwnerPlayerId ?? 0, SaveGeneration = intent.Resolution?.Scope.SaveGeneration ?? 0,
            OldHeadId = expected?.HeadId ?? Guid.Empty.ToString("D"), PredecessorId = expected?.TransitionId,
            NextHeadId = intent.Allocation.NextHeadId, CreatedAt = intent.Allocation.At,
            Intent = OutpostAdminReceiptEvidence.Binding(receipt), Effects = new byte[32], Digest = new byte[32]
        };
        return (receipt, audit, transition);
    }
}
