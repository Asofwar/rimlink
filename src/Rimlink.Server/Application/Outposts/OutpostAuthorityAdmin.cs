using Google.Protobuf;
using Microsoft.EntityFrameworkCore;

namespace Rimlink.Server.Application.Outposts;

public sealed partial class OutpostAuthority
{
    internal Task<OutpostDomainTransaction> BeginAdminActivationHeldAsync(OutpostLease lease, OutpostAdminIntent command, CancellationToken ct)
    {
        var frozen = OutpostAdminIntentCodec.Freeze(command);
        if (frozen.Action != OutpostAdminActionKind.ActivateDomainFormat) throw AdminMismatch();
        return BeginAdminHeldAsync(lease, frozen, new(1, frozen.Allocation.ReceiptId,
            OutpostAdminDurableResultKind.DomainFormatActivated, null, null, [], true), ct);
    }

    internal Task<OutpostDomainTransaction> BeginAdminManifestHeldAsync(OutpostLease lease, OutpostAdminIntent command, CancellationToken ct)
    {
        var frozen = OutpostAdminIntentCodec.Freeze(command);
        if (frozen.Action != OutpostAdminActionKind.ConfirmManifest) throw AdminMismatch();
        return BeginAdminHeldAsync(lease, frozen, new(1, frozen.Allocation.ReceiptId, OutpostAdminDurableResultKind.ManifestConfirmed,
            null, null, [new(OutpostAdminEvidenceKind.Manifest, frozen.Manifest!.ManifestId, frozen.Manifest.Digest)], true), ct);
    }

    internal Task<OutpostDomainTransaction> BeginAdminResolveHeldAsync(OutpostLease lease, OutpostAdminIntent command,
        OutpostAdminDurableResult result, CancellationToken ct)
    {
        var frozen = OutpostAdminIntentCodec.Freeze(command);
        if (frozen.Action != OutpostAdminActionKind.Resolve) throw AdminMismatch();
        return BeginAdminHeldAsync(lease, frozen, result, ct);
    }

    private Task<OutpostDomainTransaction> BeginAdminHeldAsync(OutpostLease lease, OutpostAdminIntent frozen,
        OutpostAdminDurableResult result, CancellationToken ct)
    {
        var expected = frozen.Expected.Committed!;
        var (receipt, _) = OutpostAdminReceiptEvidence.Create(frozen, result);
        var scope = frozen.Resolution?.Scope ?? frozen.Recovery?.Ordinary?.Scope ?? frozen.Recovery?.Canonical?.ViewScope;
        var intent = new TransitionIntent(frozen.Allocation.TransitionId, expected.World, scope?.OwnerPlayerId ?? 0,
            scope?.SaveGeneration ?? 0, expected.Boundary,
            OutpostAdminReceiptEvidence.TransitionKind(frozen.Action), OutpostAdminIntentCodec.ActorKey(frozen.Actor),
            new(expected.HeadId, ByteString.CopyFrom(expected.HeadDigest)), ByteString.CopyFrom(OutpostAdminReceiptEvidence.Binding(receipt)));
        return BeginCoreAsync(lease, intent, null, ct, admin: frozen);
    }

    private async Task<OutpostDomainFormat> ValidateAdminCommandHeldAsync(OutpostLease lease, OutpostAdminIntent command,
        Storage.Entities.OutpostAuthority row, StoredDomainObservation domain, CancellationToken ct)
    {
        var expected = command.Expected.Committed!;
        var tip = await db.OutpostTransitions.AsNoTracking().SingleAsync(t => t.Id == row.TransitionId, ct);
        if (expected.DomainFormat != domain.Format || expected.Installation != row.InstallationId || expected.World != row.WorldId
            || expected.Boundary != row.RestoreBoundary || expected.HeadId != row.HeadId
            || !expected.HeadDigest.AsSpan().SequenceEqual(row.HeadDigest) || expected.TransitionId != row.TransitionId
            || expected.Schema.ProfileId != domain.Schema.ProfileId || !expected.Schema.Digest.AsSpan().SequenceEqual(domain.Schema.Digest.Span)
            || await db.AdminActions.AnyAsync(a => a.Id == command.Allocation.AuditId, ct)
            || await db.OutpostAdminCommands.AnyAsync(a => a.Id == command.Allocation.ReceiptId, ct)
            || await db.OutpostTransitions.AnyAsync(t => t.Id == command.Allocation.TransitionId || t.NextHeadId == command.Allocation.NextHeadId, ct))
            throw AdminMismatch();
        var headEvidence = command.Evidence.SingleOrDefault(e => e.Kind == OutpostAdminEvidenceKind.Transition && e.Id == tip.Id);
        if (headEvidence is null || headEvidence.Id != tip.Id || headEvidence.Digest is not { Length: 32 }
            || !headEvidence.Digest.AsSpan().SequenceEqual(tip.Digest)) throw AdminMismatch();
        if (command.Action == OutpostAdminActionKind.ActivateDomainFormat)
        {
            var activation = command.Activation!;
            if (domain.Format != OutpostDomainFormat.V1 || activation.From != domain.Format || activation.To != OutpostDomainFormat.V2
                || !activation.PreviousDomainDigest.AsSpan().SequenceEqual(tip.Effects) || command.Evidence.Length != 1) throw AdminMismatch();
            return OutpostDomainFormat.V2;
        }
        if (command.Action == OutpostAdminActionKind.Resolve && command.Version == 2)
        {
            if (domain.Format != OutpostDomainFormat.V2) throw AdminMismatch();
            if (command.Recovery?.Canonical is { } canonical)
            {
                await ReadAdminCanonicalCurrentAsync(lease, canonical, ct);
                var refs = OutpostAdminResolveEvidence.CanonicalPrerequisites(tip, canonical);
                if (refs.Length != command.Evidence.Length || !refs.Zip(command.Evidence).All(p => p.First.Kind == p.Second.Kind
                    && p.First.Id == p.Second.Id && (p.First.Digest ?? []).AsSpan().SequenceEqual(p.Second.Digest))) throw AdminMismatch();
                return domain.Format;
            }
            if (command.Recovery?.Ordinary?.Input is not null) RequireRuntimeCustody();
            await ValidateOrdinaryAdminCommandAsync(command, row, ct);
            return domain.Format;
        }
        if (command.Action == OutpostAdminActionKind.Resolve)
        {
            if (command.Resolution?.Kind is OutpostAdminResolutionKind.AbortNoRemoval or OutpostAdminResolutionKind.AbortWithReturn)
                RequireRuntimeCustody();
            if (domain.Format != OutpostDomainFormat.V2 || command.Resolution is not { } selected || command.Evidence.Length != 2
                || selected.Scope.WorldInstanceId != row.WorldId || selected.Scope.RestoreBoundary != row.RestoreBoundary
                || !await db.Players.AsNoTracking().AnyAsync(p => p.Id == selected.Scope.OwnerPlayerId
                    && p.SaveGeneration == selected.Scope.SaveGeneration && p.SaveWriteEpoch == selected.Scope.SaveWriteEpoch, ct)) throw AdminMismatch();
            var source = await ReadAdminResolutionAsync(selected, ct);
            var evidence = command.Evidence.SingleOrDefault(e => e.Kind == OutpostAdminEvidenceKind.Operation);
            if (evidence?.Id != source.Id || evidence.Digest is not { Length: 32 }
                || !System.Security.Cryptography.SHA256.HashData(source.Intent).AsSpan().SequenceEqual(evidence.Digest)) throw AdminMismatch();
            return domain.Format;
        }
        if (command.Action != OutpostAdminActionKind.ConfirmManifest || domain.Format != OutpostDomainFormat.V2
            || command.Evidence.Length != 2) throw AdminMismatch();
        if (await db.PawnCustody.AnyAsync(ct)) RequireRuntimeCustody();
        var manifest = command.Evidence.SingleOrDefault(e => e.Kind == OutpostAdminEvidenceKind.Manifest);
        if (manifest is null || manifest.Id != command.Manifest!.ManifestId || manifest.Digest is not { Length: 32 }
            || !manifest.Digest.AsSpan().SequenceEqual(command.Manifest.Digest)) throw AdminMismatch();
        return domain.Format;
    }

    internal Task<Storage.Entities.OutpostOperation> ReadAdminResolutionAsync(OutpostAdminResolutionSelection selected, CancellationToken ct) =>
        OutpostAdminResolveEvidence.ReadSelectionAsync(db, files, selected, ct);

    internal Task<OutpostAdminTerminalEvidence> ReadAdminTerminalAsync(OutpostAdminResolutionSelection selected, CancellationToken ct) =>
        OutpostAdminResolveEvidence.ReadTerminalAsync(db, files, selected, ct);

    internal Task<OutpostAdminCancellationEvidence> StageAdminCancellationAsync(OutpostLease lease, OutpostDomainTransaction domain,
        OutpostAdminResolutionSelection selected, string id, DateTime at, CancellationToken ct) =>
        OutpostAssignmentService.StageAdminCancellationHeldAsync(db, files, lease, selected, id, at, ct, domain);

    private Task<StoredDomainObservation> ValidateAdminHeadAsync(Storage.Entities.OutpostAuthority row, AuthorityHead next, CancellationToken ct) =>
        ReadStoredChainAsync(db, files, row, next, currentRuntime: true, ct);

    private static OutpostEvidenceUnavailableException AdminMismatch() => new("admin-intent-or-observation-mismatch");
}
