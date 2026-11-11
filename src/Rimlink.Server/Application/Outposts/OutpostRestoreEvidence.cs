using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Rimlink.Server.Application.Saves;
using Rimlink.Server.Backup;
using Rimlink.Server.Storage;
using Rimlink.Server.Storage.Entities;
using Rimlink.Server.Application.GuildCapture;

namespace Rimlink.Server.Application.Outposts;

internal sealed record RestorePlayerMap(long PlayerId, long Generation, long WriteEpoch, SaveVersion? Canonical);
internal sealed record RestoreOutpostMap(OutpostRow Before, OutpostRow After);
internal sealed record RestoreCustodyMap(PawnCustodyRecord Before, PawnCustodyRecord After);
internal sealed record RestoreMainPlan(int Version, RestoreIntent Journal, byte[] JournalDigest, OutpostBackupManifest Cut,
    string NextHeadId, string SourceDirectory, RestorePlayerMap[] Players, RestoreOutpostMap[] Outposts, RestoreCustodyMap[] Custody, RestoreParcelMap[] Parcels,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] RestoreDomainStep? DomainStep = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] byte[]? CutManifest = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] GuildRestorePlan? Guild = null);
internal sealed record RestoreDomainStep(int Version, OutpostDomainFormat From, OutpostDomainFormat To, string Action,
    OutpostSchemaBinding Source, OutpostSchemaBinding Target,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] RestoreQualifiedMigration? Migration = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] RestoreExecutorArtifact? Executor = null);
internal sealed record RestoreMainProgress(int Version, string RootId, string? CompletionId);
internal sealed record RestoreCheckpointMap(OutpostRow Before, OutpostRow After);
internal sealed record RestoreCompletionPlan(int Version, string RestoreId, byte[] RootDigest, ClockSample Cut, RestoreCheckpointMap[] Checkpoints,
    string? PreviousCompletionId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] GuildRestoreMap<GuildOutpostRow>[]? GuildCheckpoints = null);

/// <summary>Typed restore seams. Original source bytes and rows stay immutable; current maps never authorize a transfer.</summary>
public sealed partial class OutpostRestoreEvidence(RimlinkDbContext db, SaveFiles files)
{
    private static readonly AsyncLocal<int> Depth = new();
    internal static byte[] Encode<T>(T value) => OutpostRestoreJournalCodec.Encode(value);
    internal static T Decode<T>(byte[] bytes)
    {
        if (bytes.Length is 0 or > OutpostBackupManifest.MaximumBytes) throw Invalid("restore-evidence-limit");
        try { return JsonSerializer.Deserialize<T>(bytes, OutpostBackupManifest.Json) ?? throw Invalid("restore-evidence-invalid"); }
        catch (JsonException) { throw Invalid("restore-evidence-invalid"); }
    }
    internal static bool Same<T>(T a, T b) => Encode(a).AsSpan().SequenceEqual(Encode(b));
    internal static RestoreMainPlan Plan(OutpostTransition transition)
    {
        var plan = DecodePlan(transition.Intent);
        var selected = plan.Journal.Selection; var source = plan.Cut.Authority;
        OutpostRestoreJournalCodec.ValidateSelection(selected);
        if (transition.Kind != "BulkRestore" || plan.Version is not (1 or 2 or 3) || transition.Id != selected.RestoreId
            || transition.WorldId != source.WorldId || transition.RestoreBoundary != selected.TargetBoundary
            || transition.PlayerId != 0 || transition.SaveGeneration != 0 || transition.Actor != "offline-restore"
            || transition.OldHeadId != source.HeadId || transition.PredecessorId != source.TransitionId || transition.NextHeadId != plan.NextHeadId
            || source.RestoreBoundary == selected.TargetBoundary || !Same(plan.Journal.Source, source)
            || plan.JournalDigest.Length != 32 || !SHA256.HashData(Encode(plan.Journal)).AsSpan().SequenceEqual(plan.JournalDigest)
            || !SHA256.HashData(ManifestBytes(plan)).AsSpan().SequenceEqual(plan.Journal.ManifestDigest)
            || !Same(plan.Cut.Files, plan.Journal.NewFiles) || !Same(plan.Journal.SelectionDigest, OutpostRestoreJournalCodec.Digest(selected))
            || plan.SourceDirectory != $"archive/outpost-restores/{transition.Id}/cut"
            || plan.Players.Length > 10000 || plan.Outposts.Length > 10000 || plan.Custody.Length > 10000 || plan.Parcels.Length > 10000
            || !plan.Parcels.Select(p => p.Original.ParcelId).SequenceEqual(plan.Parcels.Select(p => p.Original.ParcelId).Distinct().Order())
            || !plan.Players.Select(p => p.PlayerId).SequenceEqual(plan.Players.Select(p => p.PlayerId).Distinct().Order())
            || plan.Players.Any(p => p.PlayerId <= 0 || p.Generation < 0 || p.WriteEpoch < 0)) throw Invalid("restore-main-plan-mismatch");
        ValidateDomainStep(plan);
        OutpostRestoreFence.Validate(plan.Journal);
        return plan;
    }

    internal static async Task<RestoreMainPlan> CreatePlanAsync(RimlinkDbContext source, RestoreIntent journal,
        byte[] journalDigest, OutpostBackupManifest cut, CancellationToken ct, byte[]? cutManifest = null, int version = 1, RestoreDomainStep? recordedStep = null, bool allowPostSchema = false)
    {
        RestoreDomainStep? domainStep = null;
        if (version is 2 or 3)
        {
            if (cutManifest is null || !Same(OutpostBackupManifest.Decode(cutManifest), cut)
                || !SHA256.HashData(cutManifest).AsSpan().SequenceEqual(journal.ManifestDigest)) throw Invalid("restore-original-manifest-mismatch");
            var row = await source.OutpostAuthority.AsNoTracking().SingleAsync(ct);
            string databasePath = new SqliteConnectionStringBuilder(source.Database.GetConnectionString()).DataSource;
            var sourceFiles = new SaveFiles(DataDirectory.Existing(Path.GetDirectoryName(databasePath)!));
            var observed = await OutpostAuthority.ReadStoredChainAsync(source, sourceFiles, row,
                new(row.HeadId, Google.Protobuf.ByteString.CopyFrom(row.HeadDigest)), currentRuntime: false, ct);
            if (recordedStep is not null)
            {
                ValidateSourceDomain(recordedStep, cut, observed, allowPostSchema, journal.Selection.Isolated?.Source);
                domainStep = Decode<RestoreDomainStep>(Encode(recordedStep));
            }
            else
            {
                cut.ValidateDomain(observed, journal.Selection.Isolated?.Source);
                var schema = new OutpostSchemaBinding(observed.Schema.ProfileId, observed.Schema.Digest.ToByteArray());
                domainStep = observed.Schema.IsCurrent
                    ? new(version == 3 ? 3 : 1, observed.Format, version == 3 ? OutpostDomainFormat.V3 : OutpostDomainFormat.V2, "NoDdl", schema, schema with { Digest = schema.Digest.ToArray() })
                    : await OutpostRestoreSchemaMigration.SelectAsync(source, observed, ct);
            }
        }
        else if (version != 1 || cutManifest is not null) throw Invalid("restore-main-plan-version");
        var players = await source.Players.AsNoTracking().OrderBy(p => p.Id).ToListAsync(ct);
        if (players.Count > 10000) throw Invalid("restore-owner-map-limit");
        var owners = new List<RestorePlayerMap>();
        foreach (var player in players)
        {
            var canonical = await source.SaveVersions.AsNoTracking().SingleOrDefaultAsync(v => v.PlayerId == player.Id && v.IsCurrent, ct);
            owners.Add(new(player.Id, player.SaveGeneration, player.SaveWriteEpoch, canonical));
        }
        var allOutposts = await source.Outposts.AsNoTracking().OrderBy(o => o.Id).ToListAsync(ct);
        var allCustody = await source.PawnCustody.AsNoTracking().OrderBy(o => o.Id).ToListAsync(ct);
        bool Current(string world, string boundary, long player, long generation) => world == cut.Authority.WorldId
            && boundary == cut.Authority.RestoreBoundary && owners.Any(p => p.PlayerId == player && p.Generation == generation);
        var outposts = allOutposts.Where(o => Current(o.WorldId, o.RestoreBoundary, o.PlayerId, o.SaveGeneration)).Select(o =>
        {
            var after = Decode<OutpostRow>(Encode(o)); after.RestoreBoundary = journal.Selection.TargetBoundary; after.LastTransitionId = journal.Selection.RestoreId;
            return new RestoreOutpostMap(o, after);
        }).ToArray();
        var custody = allCustody.Where(o => Current(o.WorldId, o.RestoreBoundary, o.PlayerId, o.SaveGeneration)).Select(o =>
        {
            var after = Decode<PawnCustodyRecord>(Encode(o)); after.RestoreBoundary = journal.Selection.TargetBoundary;
            after.FrontierTransitionId = journal.Selection.RestoreId;
            return new RestoreCustodyMap(o, after);
        }).ToArray();
        var parcels = await CreateParcelMapsAsync(source, journal, cut, owners, ct);
        return new(version, journal, journalDigest, cut, Guid.NewGuid().ToString("D"),
            $"archive/outpost-restores/{journal.Selection.RestoreId}/cut", owners.ToArray(), outposts, custody, parcels, domainStep, cutManifest?.ToArray(),
            version == 3 ? await GuildRestore.CreateAsync(source, cut.Authority.WorldId, journal.Selection.RestoreId, cut.CutUtc, ct) : null);
    }

    public async Task ValidateStoredAsync(CancellationToken ct, OutpostTransition? staged = null)
    {
        if (Depth.Value >= 8) throw Invalid("restore-provenance-depth-limit");
        Depth.Value++;
        try
        {
            var transitions = await db.OutpostTransitions.AsNoTracking().ToDictionaryAsync(t => t.Id, ct);
            if (staged is not null) transitions.Add(staged.Id, staged);
            var rows = await db.OutpostLifecycleOperations.AsNoTracking().Where(r => r.Kind == "BulkRestore").ToListAsync(ct);
            var referenced = new HashSet<string>();
            var authorizations = await db.OutpostRestoreAuthorizations.AsNoTracking().ToDictionaryAsync(a => a.Id, ct);
            var authorizedParcels = new HashSet<string>();
            foreach (var row in rows)
            {
                if (!transitions.TryGetValue(row.Id, out var root)) throw Invalid("restore-main-transition-missing");
                var plan = Plan(root);
                if (!row.Intent.AsSpan().SequenceEqual(root.Intent) || row.WorldId != root.WorldId || row.RestoreBoundary != root.RestoreBoundary
                    || row.Actor != root.Actor || row.RequestId != row.Id || row.IntentDigest.Length != 32
                    || !SHA256.HashData(row.Intent).AsSpan().SequenceEqual(row.IntentDigest)) throw Invalid("restore-main-record-mismatch");
                var progress = Decode<RestoreMainProgress>(row.Progress);
                if (progress.Version != 1 || progress.RootId != root.Id) throw Invalid("restore-main-progress-mismatch");
                referenced.Add(root.Id);
                if (row.Phase == "Completed")
                {
                    if (progress.CompletionId is null || !transitions.TryGetValue(progress.CompletionId, out var completion)) throw Invalid("restore-completion-missing");
                    foreach (var step in CompletionPath(root, plan, completion.Id, transitions)) referenced.Add(step.Id);
                    if (!row.Result.AsSpan().SequenceEqual(completion.Intent)) throw Invalid("restore-completion-result-mismatch");
                }
                else if (row.Phase != "MainCommitted" || progress.CompletionId is not null || row.Result.Length != 0)
                    throw Invalid("restore-main-phase-invalid");
                string retained = Path.Combine(files.DataRoot, plan.SourceDirectory);
                await OutpostRestoreProcedure.ValidateRetainedCutAsync(retained, plan.Cut, async source =>
                {
                    var expected = await CreatePlanAsync(source, plan.Journal, plan.JournalDigest, plan.Cut, ct, plan.CutManifest, plan.Version, plan.DomainStep);
                    expected = expected with { NextHeadId = plan.NextHeadId };
                    if (!Same(expected, plan)) throw Invalid("restore-source-projection-map-mismatch");
                    await ValidateCurrentProjectionsAsync(root, plan, transitions, source, ct);
                }, ct, ManifestBytes(plan), plan.Journal.Selection.Isolated?.Source);
                await ValidateParcelMapsStoredAsync(root, plan, authorizations, authorizedParcels, ct);
                foreach (var map in plan.Custody)
                {
                    var current = await db.PawnCustody.AsNoTracking().SingleOrDefaultAsync(c => c.Id == map.After.Id, ct);
                    if (current?.FrontierTransitionId == root.Id && !Same(current, map.After)) throw Invalid("restore-custody-current-map-mismatch");
                }
                foreach (var map in plan.Outposts)
                {
                    var current = await db.Outposts.AsNoTracking().SingleOrDefaultAsync(c => c.Id == map.After.Id, ct);
                    if (current?.LastTransitionId == root.Id && !Same(current, map.After)) throw Invalid("restore-outpost-current-map-mismatch");
                }
            }
            if (authorizations.Keys.Any(id => !authorizedParcels.Contains(id))) throw Invalid("restore-parcel-authorization-orphan");
            if (transitions.Values.Any(t => t.Kind is "BulkRestore" or "RestoreCompletion" && !referenced.Contains(t.Id)))
                throw Invalid("restore-transition-orphan");
        }
        finally { Depth.Value--; }
    }

    internal static IReadOnlyList<OutpostTransition> CompletionPath(OutpostTransition root, RestoreMainPlan plan, string latest,
        IReadOnlyDictionary<string, OutpostTransition> transitions)
    {
        var path = new List<OutpostTransition>(); var visited = new HashSet<string>();
        string? cursor = latest;
        while (cursor is not null)
        {
            if (path.Count >= 10000 || !visited.Add(cursor) || !transitions.TryGetValue(cursor, out var step))
                throw Invalid("restore-completion-path-incomplete-or-cyclic");
            var value = ReadCompletion(step.Intent);
            OutpostTransition? previous = null;
            if (value.PreviousCompletionId is not null && !transitions.TryGetValue(value.PreviousCompletionId, out previous))
                throw Invalid("restore-completion-predecessor-missing");
            ValidateCompletion(root, plan, step, previous);
            path.Add(step); cursor = value.PreviousCompletionId;
        }
        return path;
    }

    internal static void ValidateCompletion(OutpostTransition root, RestoreMainPlan plan, OutpostTransition completion, OutpostTransition? previous = null)
    {
        var value = ReadCompletion(completion.Intent);
        if (completion.Kind != "RestoreCompletion" || value.Version != (plan.Version == 3 ? 2 : 1) || value.RestoreId != root.Id
            || plan.Version == 3 && value.GuildCheckpoints is null || plan.Version != 3 && value.GuildCheckpoints is not null
            || !value.RootDigest.AsSpan().SequenceEqual(root.Digest) || completion.OldHeadId != (previous?.NextHeadId ?? root.NextHeadId)
            || completion.PredecessorId != (previous?.Id ?? root.Id) || value.PreviousCompletionId != previous?.Id
            || previous is not null && previous.Kind != "RestoreCompletion"
            || completion.WorldId != root.WorldId || completion.RestoreBoundary != root.RestoreBoundary
            || completion.PlayerId != 0 || completion.SaveGeneration != 0 || completion.Actor != root.Actor
            || value.Checkpoints.Length != plan.Outposts.Length || value.Cut.Utc.Kind != DateTimeKind.Utc
            || value.Cut.Frequency <= 0 || string.IsNullOrWhiteSpace(value.Cut.ProcessId) || completion.CreatedAt != value.Cut.Utc)
            throw Invalid("restore-completion-mismatch");
        var sources = previous is null ? plan.Outposts.Select(m => m.After).ToArray()
            : ReadCompletion(previous.Intent).Checkpoints.Select(m => m.After).ToArray();
        if (sources.Length != value.Checkpoints.Length) throw Invalid("restore-completion-source-count-mismatch");
        for (int i = 0; i < value.Checkpoints.Length; i++)
        {
            var map = value.Checkpoints[i]; var expected = Decode<OutpostRow>(Encode(sources[i]));
            if (!Same(map.Before, expected)) throw Invalid("restore-completion-source-mismatch");
            if (expected.State != "Archived")
            {
                var checkpoint = OutpostClock.Advance(new(expected.CheckpointUtc, expected.ClockProcessId, expected.ClockTimestamp, expected.ClockFrequency), value.Cut);
                expected.CheckpointUtc = checkpoint.Utc; expected.ClockProcessId = checkpoint.ProcessId;
                expected.ClockTimestamp = checkpoint.Timestamp; expected.ClockFrequency = checkpoint.Frequency;
            }
            expected.LastTransitionId = completion.Id;
            if (!Same(map.After, expected)) throw Invalid("restore-completion-checkpoint-mismatch");
        }
        if (plan.Guild is not null)
        {
            var guildSources = previous is null ? plan.Guild.Outposts.Select(m => m.After).ToArray()
                : ReadCompletion(previous.Intent).GuildCheckpoints!.Select(m => m.After).ToArray();
            GuildRestore.ValidateCompletion(guildSources, value.GuildCheckpoints!, value.Cut);
        }
    }

    internal static bool CustodyMatches(OutpostTransition root, PawnCustodyRecord custody) => root.Kind == "BulkRestore"
        && Plan(root).Custody.Any(m => Same(m.After, custody));
    private static OutpostEvidenceUnavailableException Invalid(string reason) => new(reason);
}
