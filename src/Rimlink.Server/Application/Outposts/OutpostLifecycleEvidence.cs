using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Saves;
using Rimlink.Server.Storage;
using Rimlink.Server.Storage.Entities;

namespace Rimlink.Server.Application.Outposts;

// Bytes in these records belong to the protected domain, not to a client or a newly created colony.
public sealed record LifecycleOwner(byte[] Scope);
public sealed record LifecycleOutpost(string Id, string State);
public sealed record LifecycleVersion(string WorldId, long PlayerId, SaveArchive.ManifestVersion Version);
public sealed record LifecycleFile(long PlayerId, string Name, long Length, byte[] Sha256);
public sealed record LifecycleIntent(int Version, string Id, string Kind, string WorldId, string Boundary,
    string Actor, string HeadId, byte[] HeadDigest, long[] Owners, LifecycleOwner[] Scopes,
    string Directory, DateTime CutUtc, ClockSample Cut, LifecycleOutpost[] Outposts, LifecycleVersion[] Versions, LifecycleFile[] Files, long PlayerId, long ArchiveId = 0, LifecycleOwner[]? ResultScopes = null, LifecycleBinding? Binding = null, LifecycleRollback? Rollback = null);
public sealed record LifecycleProgress(int Version, string Phase, string TransitionId, string? PreviousTransitionId,
    byte[] PreviousDigest, long ArchiveId, bool SourceCleaned);
public sealed record LifecycleResult(int Version, string Outcome, long ArchiveId);
public sealed record LifecycleTransition(int Version, string OperationId, byte[] IntentDigest, string Phase,
    string? PreviousTransitionId, byte[] PreviousDigest, long ArchiveId, bool SourceCleaned);
public sealed record LifecycleStep(string OperationId, string? ExpectedPhase, byte[] IntentDigest, string NextPhase, bool Cleanup = false);

/// <summary>Strict lifecycle codec and original snapshot locators. Caller holds A/O/S; never repairs evidence.</summary>
public sealed partial class OutpostLifecycleEvidence(RimlinkDbContext db, SaveFiles files)
{
    internal const int MaxBytes = 32 * 1024 * 1024;
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 32, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false, RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true
    };
    internal static byte[] Encode<T>(T value)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        if (bytes.Length > MaxBytes) throw Invalid("lifecycle-metadata-limit");
        return bytes;
    }
    internal static T Decode<T>(byte[] bytes)
    {
        if (bytes.Length is 0 or > MaxBytes) throw Invalid("lifecycle-metadata-limit");
        try { return JsonSerializer.Deserialize<T>(bytes, Json) ?? throw Invalid("lifecycle-metadata-invalid"); }
        catch (JsonException) { throw Invalid("lifecycle-metadata-invalid"); }
    }
    internal static LifecycleIntent Intent(OutpostLifecycleOperation row)
    {
        var value = Decode<LifecycleIntent>(row.Intent);
        if (value.Version != 1 || value.Id != row.Id || !Guid.TryParseExact(value.Id, "D", out _)
            || value.Kind != row.Kind || value.Kind is not ("Archive" or "Restore" or "Reset" or "Recreate" or "WorldBind" or "Rollback")
            || value.WorldId != row.WorldId || value.Boundary != row.RestoreBoundary || value.Actor != row.Actor
            || value.HeadDigest.Length != 32 || !Guid.TryParseExact(value.HeadId, "D", out _)
            || value.Owners.Length is 0 or > 10000 || !value.Owners.SequenceEqual(value.Owners.Distinct().Order())
            || value.Owners.Any(id => id <= 0) || value.Scopes.Length != value.Owners.Length
            || value.Versions.Length > OutpostSnapshotGuard.MaxSnapshots || value.Outposts.Length > 10000 || value.Files.Length > 30000
            || row.IntentDigest.Length != 32 || !SHA256.HashData(row.Intent).AsSpan().SequenceEqual(row.IntentDigest)
            || value.Directory != Path.Combine("archive", "outpost-evidence", row.Id)) throw Invalid("lifecycle-intent-invalid");
        for (int i = 0; i < value.Scopes.Length; i++)
        {
            OutpostScopeMessage scope;
            try { scope = OutpostScopeMessage.Parser.ParseFrom(value.Scopes[i].Scope); }
            catch (InvalidProtocolBufferException) { throw Invalid("lifecycle-scope-invalid"); }
            if (scope.WorldInstanceId != row.WorldId || scope.RestoreBoundary != row.RestoreBoundary
                || scope.OwnerPlayerId != value.Owners[i] || scope.SaveGeneration < 0 || scope.SaveWriteEpoch < 0)
                throw Invalid("lifecycle-scope-invalid");
        }
        if (value.Kind is "Reset" or "Recreate")
        {
            if (value.ResultScopes is null || value.ResultScopes.Length != value.Scopes.Length
                || (value.Kind == "Recreate" ? value.PlayerId != 0 : !value.Owners.Contains(value.PlayerId))
                || value.Outposts.Length != 0 || value.ArchiveId != 0) throw Invalid("lifecycle-reset-plan-invalid");
            for (int i = 0; i < value.Scopes.Length; i++)
            {
                var expected = OutpostScopeMessage.Parser.ParseFrom(value.Scopes[i].Scope);
                if (value.Kind == "Recreate" || expected.OwnerPlayerId == value.PlayerId)
                {
                    if (expected.SaveGeneration == long.MaxValue) throw Invalid("lifecycle-generation-exhausted");
                    expected.SaveGeneration++;
                }
                if (!expected.ToByteArray().AsSpan().SequenceEqual(value.ResultScopes[i].Scope)) throw Invalid("lifecycle-reset-result-scope-invalid");
            }
        }
        else if (value.Kind == "WorldBind") ValidateBindingIntent(value);
        else if (value.Kind == "Rollback") ValidateRollbackIntent(value);
        else if (value.ResultScopes is not null) throw Invalid("lifecycle-unexpected-result-scopes");
        if (value.Kind != "WorldBind" && value.Binding is not null) throw Invalid("lifecycle-unexpected-world-binding");
        if (value.Kind != "Rollback" && value.Rollback is not null) throw Invalid("lifecycle-unexpected-rollback");
        if (value.Versions.Select(v => (v.WorldId, v.PlayerId, v.Version.SaveGeneration, v.Version.SnapshotId)).Distinct().Count() != value.Versions.Length)
            throw Invalid("lifecycle-locator-ambiguous");
        return value;
    }

    public Task ValidateStoredAsync(CancellationToken ct, OutpostTransition? staged = null) => ValidateStoredCoreAsync(ct, staged, null);

    internal async Task ValidateStoredCoreAsync(CancellationToken ct, OutpostTransition? staged, string? ownedOperation)
    {
        var transitions = await db.OutpostTransitions.AsNoTracking().ToDictionaryAsync(t => t.Id, ct);
        if (staged is not null) transitions.Add(staged.Id, staged);
        var operations = await db.OutpostLifecycleOperations.AsNoTracking().Where(r => r.Kind != "BulkRestore").ToListAsync(ct);
        var referenced = new HashSet<string>();
        foreach (var row in operations)
        {
            var intent = Intent(row);
            if (intent.Kind == "WorldBind")
            {
                await ValidateWorldBindStoredAsync(row, intent, operations, transitions, referenced, ct);
                continue;
            }
            if (intent.Kind == "Rollback")
            {
                await ValidateRollbackStoredAsync(row, intent, transitions, referenced, ct);
                continue;
            }
            var progress = Decode<LifecycleProgress>(row.Progress);
            if (progress.Version != 1 || progress.Phase != row.Phase
                || row.Phase is not ("Prepared" or "EvidenceRetained" or "Destructive" or "Completed")) throw Invalid("lifecycle-progress-invalid");
            string[] phases = ["Prepared", "EvidenceRetained", "Destructive", "Completed"];
            int count = Array.IndexOf(phases, row.Phase) + 1;
            string? previous = null; byte[] previousDigest = [];
            for (int phase = 0; phase < count; phase++)
            {
                var matches = transitions.Values.Where(t => t.Kind == "Lifecycle" && LifecycleOperation(t.Intent) == row.Id)
                    .Where(t => Decode<LifecycleTransition>(t.Intent).Phase == phases[phase]).ToArray();
                if (matches.Length != 1) throw Invalid("lifecycle-phase-evidence-missing");
                var transition = matches[0]; var evidence = Decode<LifecycleTransition>(transition.Intent);
                if (transition.WorldId != row.WorldId || transition.RestoreBoundary != row.RestoreBoundary
                    || transition.Actor != row.Actor || evidence.Version != 1 || !evidence.IntentDigest.AsSpan().SequenceEqual(row.IntentDigest)
                    || evidence.PreviousTransitionId != previous || !evidence.PreviousDigest.AsSpan().SequenceEqual(previousDigest)
                    || (phase == 0 && transition.OldHeadId != intent.HeadId)) throw Invalid("lifecycle-transition-mismatch");
                previous = transition.Id; previousDigest = transition.Digest;
                referenced.Add(transition.Id);
                if (phase == count - 1 && (progress.TransitionId != transition.Id || progress.PreviousTransitionId != evidence.PreviousTransitionId
                    || !progress.PreviousDigest.AsSpan().SequenceEqual(evidence.PreviousDigest) || progress.ArchiveId != evidence.ArchiveId
                    || progress.SourceCleaned != evidence.SourceCleaned)) throw Invalid("lifecycle-progress-mismatch");
            }
            if (intent.Kind == "Restore")
            {
                var source = operations.Where(r => r.Kind == "Archive" && r.Phase == "Completed"
                    && Decode<LifecycleProgress>(r.Progress).ArchiveId == intent.ArchiveId).ToArray();
                if (source.Length > 1 || intent.Versions.Length != 0 || intent.Files.Length != 0)
                    throw Invalid("lifecycle-restore-source-mismatch");
                if (source.Length == 1)
                {
                    var original = Intent(source[0]);
                    if (original.PlayerId != intent.PlayerId || !original.Outposts.SequenceEqual(intent.Outposts))
                        throw Invalid("lifecycle-restore-source-mismatch");
                }
                else
                {
                    if (intent.Outposts.Length != 0) throw Invalid("lifecycle-restore-source-mismatch");
                    var scope = OutpostScopeMessage.Parser.ParseFrom(intent.Scopes[Array.IndexOf(intent.Owners, intent.PlayerId)].Scope);
                    await new OutpostRestoreEvidence(db, files).ValidateLegacyCutArchiveAsync(scope, intent.ArchiveId, null, ct);
                }
            }
            if (row.Phase != "Prepared") await VerifyLocators(intent, ct);
            if (row.Phase == "Completed")
            {
                var result = Decode<LifecycleResult>(row.Result);
                if (result.Version != 1 || result.Outcome != (intent.Kind == "Recreate" ? "NoActiveWorld" : intent.Kind) || result.ArchiveId != progress.ArchiveId
                    || !progress.SourceCleaned) throw Invalid("lifecycle-result-invalid");
            }
            else if (row.Result.Length != 0) throw Invalid("lifecycle-result-premature");
            if (row.Phase != "Completed" && ownedOperation != row.Id) throw Invalid("lifecycle-operation-unfinished");
        }
        if (transitions.Values.Any(t => (t.Kind is "Lifecycle" or "WorldBind" or "Rollback") && !referenced.Contains(t.Id))) throw Invalid("lifecycle-transition-orphan");
        var worldOperation = await LatestWorldOperationAsync(ct, operations, transitions);
        if (worldOperation is { Kind: "Recreate", Phase: "Destructive" or "Completed" } && await db.World.AnyAsync(ct))
            throw Invalid("lifecycle-retired-world-projection-mismatch");
        if (worldOperation is { Kind: "WorldBind" }) await ValidateCurrentWorldAsync(Intent(worldOperation).Binding!.Target, ct);
        if (worldOperation is null)
        {
            var head = await db.OutpostAuthority.AsNoTracking().SingleAsync(ct);
            var worlds = await db.World.AsNoTracking().ToListAsync(ct);
            if (worlds.Count != 1 || worlds[0].InfluenceInstanceId != head.WorldId)
                throw Invalid("lifecycle-initial-world-projection-mismatch");
        }
        // Only the newest lifecycle for an owner imposes a present projection; old terminal epochs are history.
        foreach (long owner in operations.SelectMany(r => Intent(r).Owners).Distinct())
        {
            var latest = LatestForOwner(owner, operations, transitions);
            var intent = Intent(latest);
            var currentScopes = (intent.Kind is "Reset" or "Recreate" or "WorldBind" or "Rollback") && (latest.Phase is "Destructive" or "Completed") ? intent.ResultScopes! : intent.Scopes;
            var scope = OutpostScopeMessage.Parser.ParseFrom(currentScopes[Array.IndexOf(intent.Owners, owner)].Scope);
            var player = await db.Players.AsNoTracking().SingleOrDefaultAsync(p => p.Id == owner, ct);
            if (player is null || player.SaveGeneration != scope.SaveGeneration || player.SaveWriteEpoch != scope.SaveWriteEpoch)
                throw Invalid("lifecycle-owner-projection-mismatch");
            // Cleanup participants constrain scope, but do not replace that participant's own archive/reset projection.
            var targetOperations = operations.Where(r => r.Kind == "Recreate" || Intent(r).PlayerId == owner).ToArray();
            if (targetOperations.Length == 0) continue;
            latest = LatestForOwner(owner, targetOperations, transitions);
            intent = Intent(latest);
            if (intent.Kind == "Rollback" && !RestoreSupersedesProjection(latest, owner, transitions))
                await ValidateRollbackProjectionAsync(intent, scope, ct);
            if ((intent.Kind is "Reset" or "Recreate") && (latest.Phase is "Destructive" or "Completed"))
            {
                var versions = await db.SaveVersions.AsNoTracking().Where(v => v.PlayerId == owner).ToListAsync(ct);
                if (versions.Count == 0)
                {
                    if (await db.WorldObjects.AnyAsync(o => o.OwnerPlayerId == owner, ct) || player.LastOutgoingTick != 0
                        || player.FirstSaveAt is not null || player.WealthTotal != 0 || player.WealthItems != 0)
                        throw Invalid("lifecycle-reset-projection-mismatch");
                }
                else
                {
                    if (latest.Phase != "Completed" || versions.Count(v => v.IsCurrent) != 1)
                        throw Invalid("lifecycle-reset-projection-mismatch");
                    foreach (var version in versions)
                    {
                        if (version.SaveGeneration != scope.SaveGeneration || version.SaveWriteEpoch != scope.SaveWriteEpoch
                            || version.SnapshotId is null || version.RawSha256?.Length != 32)
                            throw Invalid("lifecycle-reset-projection-mismatch");
                        var link = await db.OutpostSnapshotLinks.AsNoTracking().SingleOrDefaultAsync(l => l.WorldId == scope.WorldInstanceId
                            && l.PlayerId == owner && l.SaveGeneration == scope.SaveGeneration && l.SaveWriteEpoch == scope.SaveWriteEpoch
                            && l.RestoreBoundary == scope.RestoreBoundary && l.SnapshotId == version.SnapshotId, ct);
                        if (link is null || !link.RawSha256.AsSpan().SequenceEqual(version.RawSha256))
                            throw Invalid("lifecycle-reset-projection-mismatch");
                    }
                }
            }
            if (latest.Phase == "Completed" && intent.Kind is "Archive" or "Restore")
            {
                long archiveId = Decode<LifecycleProgress>(latest.Progress).ArchiveId;
                var archive = await db.ArchivedColonies.AsNoTracking().SingleOrDefaultAsync(a => a.Id == archiveId, ct);
                if (archive is null || archive.SaveGeneration != scope.SaveGeneration || archive.PlayerId != owner)
                    throw Invalid("lifecycle-archive-projection-mismatch");
                bool archived = intent.Kind == "Archive";
                if (archive.State != (archived ? ArchiveState.Archived : ArchiveState.Restored)
                    || archived && await db.SaveVersions.AnyAsync(v => v.PlayerId == owner, ct)) throw Invalid("lifecycle-archive-projection-mismatch");
                foreach (var expected in intent.Outposts)
                {
                    var outpost = await db.Outposts.AsNoTracking().SingleOrDefaultAsync(o => o.Id == expected.Id, ct);
                    if (outpost is null || archived && outpost.State != "Archived") throw Invalid("lifecycle-outpost-projection-mismatch");
                }
            }
        }
    }

    internal async Task<bool> IsNoActiveWorldAsync(CancellationToken ct)
    {
        var latest = await LatestWorldOperationAsync(ct);
        return latest is { Kind: "Recreate", Phase: "Completed" } && !await db.World.AnyAsync(ct);
    }

    internal async Task<OutpostLifecycleOperation?> LatestWorldOperationAsync(CancellationToken ct,
        IReadOnlyList<OutpostLifecycleOperation>? operations = null, IReadOnlyDictionary<string, OutpostTransition>? transitions = null)
    {
        var rows = (operations ?? await db.OutpostLifecycleOperations.AsNoTracking().ToListAsync(ct))
            .Where(r => r.Kind is "Recreate" or "WorldBind").ToArray();
        if (rows.Length == 0) return null;
        var chain = transitions ?? await db.OutpostTransitions.AsNoTracking().ToDictionaryAsync(t => t.Id, ct);
        return LatestForOwner(Intent(rows[0]).Owners[0], rows, chain);
    }

    internal static OutpostLifecycleOperation LatestForOwner(long owner, IReadOnlyList<OutpostLifecycleOperation> operations,
        IReadOnlyDictionary<string, OutpostTransition> transitions)
    {
        var roots = transitions.Values.Where(t => t.PredecessorId is null).ToArray();
        var groups = transitions.Values.Where(t => t.PredecessorId is not null).GroupBy(t => t.PredecessorId!).ToArray();
        if (roots.Length != 1 || groups.Any(g => g.Count() != 1)) throw Invalid("lifecycle-chain-ambiguous");
        var next = groups.ToDictionary(g => g.Key, g => g.Single());
        var position = new Dictionary<string, int>(StringComparer.Ordinal);
        OutpostTransition? cursor = roots[0];
        while (cursor is not null)
        {
            if (!position.TryAdd(cursor.Id, position.Count)) throw Invalid("lifecycle-chain-cycle");
            cursor = next.GetValueOrDefault(cursor.Id);
        }
        if (position.Count != transitions.Count) throw Invalid("lifecycle-chain-disconnected");
        var candidates = operations.Where(r => Intent(r).Owners.Contains(owner)).ToArray();
        if (candidates.Length == 0) throw Invalid("lifecycle-owner-history-missing");
        return candidates.OrderByDescending(r => position.GetValueOrDefault(Decode<LifecycleProgress>(r.Progress).TransitionId, -1)).First();
    }

    // A later typed restore validates its exact selected cut/current canonical separately; old Q proofs remain mandatory history.
    private static bool RestoreSupersedesProjection(OutpostLifecycleOperation operation, long owner,
        IReadOnlyDictionary<string, OutpostTransition> transitions)
    {
        if (operation.Phase != "Completed") return false;
        string completed = Decode<LifecycleProgress>(operation.Progress).TransitionId;
        foreach (var root in transitions.Values.Where(t => t.Kind == "BulkRestore"))
        {
            if (!OutpostRestoreEvidence.Plan(root).Players.Any(p => p.PlayerId == owner)) continue;
            var visited = new HashSet<string>(StringComparer.Ordinal);
            string? cursor = root.PredecessorId;
            while (cursor is not null)
            {
                if (!visited.Add(cursor) || !transitions.TryGetValue(cursor, out var step)) throw Invalid("lifecycle-chain-disconnected-or-cycle");
                if (cursor == completed) return true;
                cursor = step.PredecessorId;
            }
        }
        return false;
    }

    private static string LifecycleOperation(byte[] bytes) => Decode<LifecycleTransition>(bytes).OperationId;

    internal async Task VerifyLocators(LifecycleIntent intent, CancellationToken ct)
    {
        string directory = files.EvidenceArchiveDirectory(intent.Directory);
        foreach (var file in intent.Files)
        {
            ct.ThrowIfCancellationRequested();
            if ((intent.Kind == "Recreate" ? !intent.Owners.Contains(file.PlayerId) : file.PlayerId != intent.PlayerId) || file.Name != Path.GetFileName(file.Name) || file.Name.Length is 0 or > 128
                || file.Length <= 0 || file.Length > Rimlink.Protocol.Limits.MaxSaveBytes || file.Sha256.Length != 32) throw Invalid("lifecycle-file-invalid");
            string path = Path.Combine(directory, file.PlayerId.ToString(System.Globalization.CultureInfo.InvariantCulture), file.Name);
            using var stream = File.OpenRead(path);
            if (stream.Length != file.Length || !SHA256.HashData(stream).AsSpan().SequenceEqual(file.Sha256)) throw Invalid("lifecycle-retained-file-mismatch");
        }
        foreach (var locator in intent.Versions)
        {
            ct.ThrowIfCancellationRequested();
            var version = Version(locator);
            try { _ = files.ReadEvidence(version, Path.Combine(directory, locator.PlayerId.ToString(System.Globalization.CultureInfo.InvariantCulture))); }
            catch (Exception e) when (e is IOException or InvalidDataException or FormatException or ZstdSharp.ZstdException)
            { throw Invalid("lifecycle-retained-bytes-unavailable"); }
        }
        await Task.CompletedTask;
    }

    public async Task<(SaveVersion Version, string? Archive)> ResolveSnapshotAsync(OutpostSnapshotLink link, CancellationToken ct)
    {
        var live = await db.SaveVersions.AsNoTracking().Where(v => v.PlayerId == link.PlayerId && v.SaveGeneration == link.SaveGeneration
            && v.SnapshotId == link.SnapshotId).ToListAsync(ct);
        if (live.Count == 1) return (live[0], null);
        if (live.Count != 0) throw Invalid("snapshot-version-ambiguous");
        // The protected locator switch precedes removal of every old row/file.
        var rows = await db.OutpostLifecycleOperations.AsNoTracking().Where(r => r.Kind != "BulkRestore" && r.Phase != "Prepared").ToListAsync(ct);
        foreach (var row in rows.OrderByDescending(r => r.CreatedAt).ThenByDescending(r => r.Id))
        {
            var intent = Intent(row);
            var matches = intent.Versions.Where(v => v.WorldId == link.WorldId && v.PlayerId == link.PlayerId
                && v.Version.SaveGeneration == link.SaveGeneration && v.Version.SaveWriteEpoch == link.SaveWriteEpoch
                && v.Version.SnapshotId == link.SnapshotId).ToArray();
            if (matches.Length > 1) throw Invalid("snapshot-lifecycle-ambiguous");
            if (matches.Length == 1) return (Version(matches[0]), Path.Combine(files.EvidenceArchiveDirectory(intent.Directory), link.PlayerId.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }
        var archives = await db.ArchivedColonies.AsNoTracking().Where(a => a.PlayerId == link.PlayerId && a.SaveGeneration == link.SaveGeneration).ToListAsync(ct);
        (SaveVersion, string)? selected = null;
        foreach (var row in archives)
        {
            string dir = files.EvidenceArchiveDirectory(row.Dir), path = Path.Combine(dir, SaveArchive.ManifestFile);
            if (!File.Exists(path)) continue;
            if (new FileInfo(path).Length > 64 * 1024 * 1024) throw Invalid("snapshot-archive-manifest-limit");
            var manifest = JsonSerializer.Deserialize<SaveArchive.Manifest>(await File.ReadAllTextAsync(path, ct), new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (manifest is null || manifest.PlayerId != link.PlayerId) throw Invalid("snapshot-archive-owner-mismatch");
            foreach (var v in manifest.Versions.Where(v => v.SnapshotId == link.SnapshotId && v.SaveGeneration == link.SaveGeneration))
            {
                if (selected is not null) throw Invalid("snapshot-archive-ambiguous");
                selected = (Version(new(link.WorldId, link.PlayerId, v)), dir);
            }
        }
        return selected ?? throw Invalid("snapshot-version-missing");
    }

    internal static SaveVersion Version(LifecycleVersion v) => new()
    {
        PlayerId = v.PlayerId, Number = v.Version.Number, GameTick = v.Version.GameTick, Size = v.Version.Size,
        Sha256 = Convert.FromHexString(v.Version.Sha256), SnapshotId = v.Version.SnapshotId,
        SaveGeneration = v.Version.SaveGeneration, SaveWriteEpoch = v.Version.SaveWriteEpoch,
        RecoveryVersion = v.Version.RecoveryVersion, RawSha256 = v.Version.RawSha256, RecoveryJson = v.Version.RecoveryJson,
        IsCurrent = v.Version.IsCurrent, CreatedAt = v.Version.CreatedAt, DevMode = v.Version.DevMode,
        WealthTotal = v.Version.WealthTotal, WealthItems = v.Version.WealthItems
    };
    private static OutpostEvidenceUnavailableException Invalid(string reason) => new(reason);
}
