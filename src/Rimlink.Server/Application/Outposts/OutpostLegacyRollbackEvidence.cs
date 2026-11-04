using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Saves;
using Rimlink.Server.Storage.Entities;

namespace Rimlink.Server.Application.Outposts;

public sealed record LifecycleLegacySnapshot(LifecycleVersion Locator, byte[] RawSha256);
public sealed record LifecycleLegacyRollback(LifecycleLegacySnapshot Target, LifecycleLegacySnapshot PreviousCurrent);

public sealed partial class OutpostLifecycleEvidence
{
    // Absence is namespace-wide and across all epochs, including tombstones, received mail and historical senders.
    // Mechanical lifecycle/legacy-rollback rows alone do not confer game custody.
    internal async Task<bool> HasLocalProtectedHistoryAsync(OutpostScopeMessage scope, CancellationToken ct)
    {
        string world = scope.WorldInstanceId, boundary = scope.RestoreBoundary; long owner = scope.OwnerPlayerId, generation = scope.SaveGeneration;
        foreach (var version in await db.SaveVersions.AsNoTracking().Where(v => v.PlayerId == owner && v.SaveGeneration == generation).ToListAsync(ct))
        {
            var recovery = SaveRecoveryData.ReadXml(new MemoryStream(files.ReadEvidence(version), false), out _);
            if (recovery.Outposts.Present) return true;
        }
        return await db.OutpostSnapshotLinks.AnyAsync(r => r.WorldId == world && r.PlayerId == owner && r.SaveGeneration == generation && r.RestoreBoundary == boundary, ct)
            || await db.PawnCustody.AnyAsync(r => r.WorldId == world && r.PlayerId == owner && r.SaveGeneration == generation && r.RestoreBoundary == boundary, ct)
            || await db.Outposts.AnyAsync(r => r.WorldId == world && r.PlayerId == owner && r.SaveGeneration == generation && r.RestoreBoundary == boundary, ct)
            || await db.OutpostOperations.AnyAsync(r => r.WorldId == world && r.PlayerId == owner && r.SaveGeneration == generation && r.RestoreBoundary == boundary, ct)
            || await db.OutpostParcelLinks.AnyAsync(r => r.WorldId == world && r.PlayerId == owner && r.SaveGeneration == generation && r.RestoreBoundary == boundary, ct)
            || await db.OutpostDeliveryAttempts.AnyAsync(r => r.WorldId == world && r.PlayerId == owner && r.SaveGeneration == generation && r.RestoreBoundary == boundary, ct)
            || await db.OutpostOrdinaryReturns.AnyAsync(r => r.WorldId == world && r.PlayerId == owner && r.SaveGeneration == generation && r.RestoreBoundary == boundary, ct)
            || await db.OutpostTransitions.AnyAsync(r => r.WorldId == world && r.PlayerId == owner && r.SaveGeneration == generation && r.RestoreBoundary == boundary
                && r.Kind != "Lifecycle" && r.Kind != "Rollback" && r.Kind != "WorldBind", ct);
    }

    internal LifecycleLegacySnapshot ReadLegacySnapshot(SaveVersion version, string world, string? directory = null)
    {
        var recovery = SaveRecoveryData.ReadXml(new MemoryStream(files.ReadEvidence(version, directory), false), out byte[] hash);
        if (recovery.Outposts.Present || recovery.Version != version.RecoveryVersion
            || recovery.Version > 0 && (recovery.SnapshotId != version.SnapshotId || recovery.Generation != version.SaveGeneration || recovery.WriteEpoch != version.SaveWriteEpoch)
            || version.RawSha256 is not null && !version.RawSha256.AsSpan().SequenceEqual(hash)
            || version.RecoveryJson is not null && JsonSerializer.Serialize(recovery) != version.RecoveryJson)
            throw Invalid("lifecycle-legacy-snapshot-mismatch");
        return new(new(world, version.PlayerId, new(version.Number, version.GameTick, version.Size, Convert.ToHexString(version.Sha256),
            version.CreatedAt, version.IsCurrent, version.DevMode, version.WealthTotal, version.WealthItems, version.SnapshotId,
            version.SaveGeneration, version.SaveWriteEpoch, version.RecoveryVersion, version.RawSha256, version.RecoveryJson)), hash);
    }

    private static void ValidateLegacyRollbackIntent(LifecycleIntent intent, OutpostScopeMessage source)
    {
        var rollback = intent.Rollback!; var legacy = rollback.Legacy!;
        if (rollback.Kind != "LegacySnapshot" || rollback.Target.Length != 0 || rollback.PreviousCurrent.Length != 0 || rollback.FrontierDigest.Length != 0
            || intent.Versions.Length is < 1 or > 2 || intent.Files.Length is < 1 or > 4) throw Invalid("lifecycle-legacy-rollback-shape");
        foreach (var snapshot in new[] { legacy.Target, legacy.PreviousCurrent })
        {
            var locator = snapshot.Locator; var version = locator.Version;
            if (snapshot.RawSha256.Length != 32 || locator.PlayerId != source.OwnerPlayerId || locator.WorldId != source.WorldInstanceId
                || version.SaveGeneration != source.SaveGeneration || version.SaveWriteEpoch < 0 || version.SaveWriteEpoch > source.SaveWriteEpoch
                || version.Number <= 0 || version.Size <= 0 || version.Size > Limits.MaxSaveBytes
                || version.Sha256.Length != 64 || !intent.Versions.Any(v => Encode(v).AsSpan().SequenceEqual(Encode(locator))))
                throw Invalid("lifecycle-legacy-rollback-identity");
        }
        if (intent.Versions.Any(v => !Encode(v).AsSpan().SequenceEqual(Encode(legacy.Target.Locator))
            && !Encode(v).AsSpan().SequenceEqual(Encode(legacy.PreviousCurrent.Locator)))) throw Invalid("lifecycle-legacy-rollback-locator");
    }

    private async Task ValidateLegacyRollbackStoredAsync(LifecycleIntent intent, CancellationToken ct)
    {
        await VerifyLocators(intent, ct);
        string directory = Path.Combine(files.EvidenceArchiveDirectory(intent.Directory), intent.PlayerId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        foreach (var snapshot in new[] { intent.Rollback!.Legacy!.Target, intent.Rollback.Legacy.PreviousCurrent })
        {
            var observed = ReadLegacySnapshot(Version(snapshot.Locator), intent.WorldId, directory);
            if (!Encode(observed).AsSpan().SequenceEqual(Encode(snapshot))) throw Invalid("lifecycle-legacy-retained-mismatch");
        }
    }

    private async Task ValidateLegacyRollbackProjectionAsync(LifecycleIntent intent, OutpostScopeMessage scope, SaveVersion version, CancellationToken ct)
    {
        var target = intent.Rollback!.Legacy!.Target; var original = Version(target.Locator);
        if (version.Number == original.Number && version.SnapshotId == original.SnapshotId && version.SaveWriteEpoch == original.SaveWriteEpoch)
        {
            var observed = ReadLegacySnapshot(version, intent.WorldId);
            if (version.SaveGeneration != original.SaveGeneration || !observed.RawSha256.AsSpan().SequenceEqual(target.RawSha256)
                || !version.Sha256.AsSpan().SequenceEqual(original.Sha256)) throw Invalid("lifecycle-legacy-current-mismatch");
            return;
        }
        if (version.SaveGeneration != scope.SaveGeneration || version.SaveWriteEpoch != scope.SaveWriteEpoch)
            throw Invalid("lifecycle-legacy-current-mismatch");
        // Later real fresh saves stay ordinary; a later genuine first protected capture requires its own accepted link.
        var recovery = SaveRecoveryData.ReadXml(new MemoryStream(files.ReadEvidence(version), false), out byte[] hash);
        if (!recovery.Outposts.Present) _ = ReadLegacySnapshot(version, intent.WorldId);
        else
        {
            if (!recovery.Outposts.Metadata.Scope.Equals(scope) || version.RawSha256 is null || !hash.AsSpan().SequenceEqual(version.RawSha256))
                throw Invalid("lifecycle-legacy-current-mismatch");
            _ = await RollbackLinkAsync(new() { Scope = scope, SnapshotId = version.SnapshotId!, RawSha256 = Google.Protobuf.ByteString.CopyFrom(hash) }, ct);
        }
    }
}
