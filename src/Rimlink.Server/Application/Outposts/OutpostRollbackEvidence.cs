using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Rimlink.Protocol.V1;
using Rimlink.Server.Storage.Entities;

namespace Rimlink.Server.Application.Outposts;

public sealed record LifecycleRollbackSource(byte[] Reference, byte[] Provenance);
public sealed record LifecycleRollback(byte[] Target, byte[] PreviousCurrent, byte[] FrontierDigest, string Kind = "ProtectedSnapshot",
    LifecycleLegacyRollback? Legacy = null, LifecycleRollbackSource? TargetSource = null, LifecycleRollbackSource? PreviousSource = null);

public sealed partial class OutpostLifecycleEvidence
{
    private static bool SameRollbackNamespace(OutpostScopeMessage a, OutpostScopeMessage b) =>
        a.WorldInstanceId == b.WorldInstanceId && a.OwnerPlayerId == b.OwnerPlayerId && a.SaveGeneration == b.SaveGeneration
        && a.RestoreBoundary == b.RestoreBoundary;

    private static void ValidateRollbackIntent(LifecycleIntent value)
    {
        if (value.Rollback is null || value.PlayerId <= 0 || value.Owners.Length != 1 || value.Owners[0] != value.PlayerId
            || value.ResultScopes is not { Length: 1 } || value.ArchiveId != 0 || value.Outposts.Length != 0
            || value.Rollback.Kind is not ("ProtectedSnapshot" or "LegacySnapshot"))
            throw Invalid("lifecycle-rollback-intent-invalid");
        var source = OutpostScopeMessage.Parser.ParseFrom(value.Scopes[0].Scope);
        var expected = source.Clone();
        if (source.SaveWriteEpoch == long.MaxValue) throw Invalid("lifecycle-epoch-exhausted");
        expected.SaveWriteEpoch++;
        if (!expected.ToByteArray().AsSpan().SequenceEqual(value.ResultScopes[0].Scope)) throw Invalid("lifecycle-rollback-result-scope");
        if (value.Rollback.Kind == "LegacySnapshot")
        {
            if (value.Rollback.Legacy is null || value.Rollback.TargetSource is not null || value.Rollback.PreviousSource is not null) throw Invalid("lifecycle-legacy-rollback-shape");
            ValidateLegacyRollbackIntent(value, source); return;
        }
        if (value.Rollback.Legacy is not null || value.Versions.Length != 0 || value.Files.Length != 0 || value.Rollback.FrontierDigest.Length != 32)
            throw Invalid("lifecycle-rollback-intent-invalid");
        foreach (var (bytes, authorization) in new[] { (value.Rollback.Target, value.Rollback.TargetSource), (value.Rollback.PreviousCurrent, value.Rollback.PreviousSource) })
        {
            if (bytes.Length is 0 or > 4096) throw Invalid("lifecycle-rollback-target-shape");
            var proof = OutpostSnapshotProof.Parser.ParseFrom(bytes);
            if (!Guid.TryParseExact(proof.SnapshotId, "N", out _) || proof.RawSha256.Length != 32 || proof.Scope is null
                || proof.Scope.SaveWriteEpoch < 0)
                throw Invalid("lifecycle-rollback-target-shape");
            var effective = OutpostRestoreEvidence.RollbackEffectiveScope(proof, authorization);
            if (!SameRollbackNamespace(effective, source) || effective.SaveWriteEpoch < 0 || effective.SaveWriteEpoch > source.SaveWriteEpoch)
                throw Invalid("lifecycle-rollback-target-shape");
        }
    }

    internal async Task<OutpostSnapshotLink> RollbackLinkAsync(OutpostSnapshotProof proof, CancellationToken ct)
    {
        var scope = proof.Scope;
        var link = await db.OutpostSnapshotLinks.AsNoTracking().SingleOrDefaultAsync(l => l.WorldId == scope.WorldInstanceId
            && l.PlayerId == scope.OwnerPlayerId && l.SaveGeneration == scope.SaveGeneration && l.SaveWriteEpoch == scope.SaveWriteEpoch
            && l.RestoreBoundary == scope.RestoreBoundary && l.SnapshotId == proof.SnapshotId, ct);
        if (link is null || !link.RawSha256.AsSpan().SequenceEqual(proof.RawSha256.Span)) throw Invalid("lifecycle-rollback-target-proof-mismatch");
        return link;
    }

    internal async Task ValidateRollbackStoredAsync(OutpostLifecycleOperation row, LifecycleIntent intent,
        IReadOnlyDictionary<string, OutpostTransition> transitions, HashSet<string> referenced, CancellationToken ct)
    {
        if (intent.Rollback!.Kind == "LegacySnapshot") await ValidateLegacyRollbackStoredAsync(intent, ct);
        else
        {
            var target = await RollbackLinkAsync(OutpostSnapshotProof.Parser.ParseFrom(intent.Rollback.Target), ct);
            _ = await RollbackLinkAsync(OutpostSnapshotProof.Parser.ParseFrom(intent.Rollback.PreviousCurrent), ct);
            if (!target.FrontierDigest.AsSpan().SequenceEqual(intent.Rollback.FrontierDigest)) throw Invalid("lifecycle-rollback-frontier-mismatch");
            var resolver = new OutpostRestoreEvidence(db, files);
            await resolver.ValidateRollbackSourceAsync(OutpostSnapshotProof.Parser.ParseFrom(intent.Rollback.Target), intent.Rollback.TargetSource, ct);
            await resolver.ValidateRollbackSourceAsync(OutpostSnapshotProof.Parser.ParseFrom(intent.Rollback.PreviousCurrent), intent.Rollback.PreviousSource, ct);
        }
        var matches = transitions.Values.Where(t => t.Kind == "Rollback" && LifecycleOperation(t.Intent) == row.Id).ToArray();
        if (matches.Length != 1 || row.Phase != "Completed") throw Invalid("lifecycle-rollback-transition-missing");
        var transition = matches[0]; var evidence = Decode<LifecycleTransition>(transition.Intent);
        var progress = Decode<LifecycleProgress>(row.Progress); var result = Decode<LifecycleResult>(row.Result);
        var source = OutpostScopeMessage.Parser.ParseFrom(intent.Scopes[0].Scope);
        if (transition.WorldId != row.WorldId || transition.RestoreBoundary != row.RestoreBoundary || transition.PlayerId != intent.PlayerId
            || transition.SaveGeneration != source.SaveGeneration || transition.Actor != row.Actor || transition.OldHeadId != intent.HeadId
            || evidence.Version != 1 || evidence.OperationId != row.Id || evidence.Phase != "Completed" || evidence.PreviousTransitionId is not null
            || evidence.PreviousDigest.Length != 0 || evidence.ArchiveId != 0 || !evidence.SourceCleaned
            || !evidence.IntentDigest.AsSpan().SequenceEqual(row.IntentDigest) || progress.Version != 1 || progress.Phase != "Completed"
            || progress.TransitionId != transition.Id || progress.PreviousTransitionId is not null || progress.PreviousDigest.Length != 0
            || progress.ArchiveId != 0 || !progress.SourceCleaned || result != new LifecycleResult(1, "Rollback", 0))
            throw Invalid("lifecycle-rollback-transition-mismatch");
        referenced.Add(transition.Id);
    }

    private async Task ValidateRollbackProjectionAsync(LifecycleIntent intent, OutpostScopeMessage currentScope, CancellationToken ct)
    {
        var current = await db.SaveVersions.AsNoTracking().Where(v => v.PlayerId == intent.PlayerId && v.IsCurrent).ToListAsync(ct);
        if (current.Count != 1) throw Invalid("lifecycle-rollback-current-mismatch");
        var version = current[0];
        if (intent.Rollback!.Kind == "LegacySnapshot") { await ValidateLegacyRollbackProjectionAsync(intent, currentScope, version, ct); return; }
        var target = OutpostSnapshotProof.Parser.ParseFrom(intent.Rollback.Target);
        if (version.SnapshotId == target.SnapshotId)
        {
            if (version.SaveGeneration != target.Scope.SaveGeneration || version.SaveWriteEpoch != target.Scope.SaveWriteEpoch
                || version.RawSha256 is null || !version.RawSha256.AsSpan().SequenceEqual(target.RawSha256.Span))
                throw Invalid("lifecycle-rollback-current-mismatch");
        }
        else
        {
            if (version.SaveGeneration != currentScope.SaveGeneration || version.SaveWriteEpoch != currentScope.SaveWriteEpoch
                || version.SnapshotId is null || version.RawSha256?.Length != 32) throw Invalid("lifecycle-rollback-current-mismatch");
            _ = await RollbackLinkAsync(new() { Scope = currentScope, SnapshotId = version.SnapshotId,
                RawSha256 = ByteString.CopyFrom(version.RawSha256) }, ct);
        }
    }
}
