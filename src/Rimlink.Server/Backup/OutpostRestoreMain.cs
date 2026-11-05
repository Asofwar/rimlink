using System.Security.Cryptography;
using System.Text.Json;
using Google.Protobuf;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Application.Saves;
using Rimlink.Server.Storage;
using Rimlink.Server.Storage.Entities;
using OutpostAuthority = Rimlink.Server.Application.Outposts.OutpostAuthority;

namespace Rimlink.Server.Backup;

public sealed partial class OutpostRestoreProcedure
{
    private readonly OutpostClock clock;

    public OutpostRestoreProcedure(TimeProvider? time = null) => clock = new(time ?? TimeProvider.System);

    private async Task<RestoreResult> CommitMainAsync(RestoreIntent intent, RestoreJournal journal, string journalRoot,
        OutpostAnchorStore anchor, OutpostAnchorStore.OutpostAnchorLease held, CancellationToken ct)
    {
        var selected = intent.Selection;
        if (journal.Phase is RestorePhase.InstalledVerified or RestorePhase.MainPrepared)
        {
            if (anchor.ReadEnvelope(held) != OutpostRestoreFence.Marker(intent, journal.IntentDigest))
                throw new InvalidDataException("restore-main-marker-mismatch");
        }
        string staged = Path.Combine(selected.DataDirectory, "restore-work", selected.RestoreId, "staged");
        string planPath = Path.Combine(journalRoot, "main.json");
        RestoreMainPlan plan;
        if (journal.Phase == RestorePhase.InstalledVerified)
        {
            await VerifyInventoryAsync(selected.DataDirectory, intent.NewFiles, ct);
            byte[] manifestBytes = ReadJournalBytes(Path.Combine(staged, OutpostBackupManifest.FileName));
            if (!SHA256.HashData(manifestBytes).AsSpan().SequenceEqual(intent.ManifestDigest)) throw new InvalidDataException("restore-cut-manifest-changed");
            var manifest = JsonSerializer.Deserialize<OutpostBackupManifest>(manifestBytes, OutpostBackupManifest.Json)
                ?? throw new InvalidDataException("restore-cut-manifest-missing");
            await using (var source = OpenRestoreDatabase(staged, readOnly: true))
                plan = await OutpostRestoreEvidence.CreatePlanAsync(source, intent, journal.IntentDigest, manifest, ct);
            if (File.Exists(planPath))
            {
                plan = OutpostRestoreEvidence.Decode<RestoreMainPlan>(ReadJournalBytes(planPath));
                if (!OutpostRestoreEvidence.Same(plan.Journal, intent)) throw new InvalidDataException("restore-main-plan-changed");
            }
            else OutpostRestoreJournalCodec.WriteDurable(planPath, OutpostRestoreEvidence.Encode(plan));
            journal = journal with { Phase = RestorePhase.MainPrepared, Revision = checked(journal.Revision + 1) };
            StoreJournal(journalRoot, journal);
            Checkpoint?.Invoke("AfterMainPrepared");
        }
        else plan = OutpostRestoreEvidence.Decode<RestoreMainPlan>(ReadJournalBytes(planPath));
        if (!OutpostRestoreEvidence.Same(plan.Journal, intent) || !plan.JournalDigest.AsSpan().SequenceEqual(journal.IntentDigest))
            throw new InvalidDataException("restore-main-journal-mismatch");
        var rootTemplate = RootTransition(plan);
        _ = OutpostRestoreEvidence.Plan(rootTemplate);
        string sourceCut = Path.Combine(selected.DataDirectory, plan.SourceDirectory);
        if (journal.Phase == RestorePhase.MainPrepared)
        {
            // This extra source set is declared by the durable main plan before its first file effect.
            await ValidatePartialInventoryAsync(sourceCut, intent.NewFiles, ct);
            await CopyInventoryAsync(staged, sourceCut, intent.NewFiles, ct, resumeCopy: true);
            byte[] bytes = plan.Cut.Encode();
            string manifestPath = Path.Combine(sourceCut, OutpostBackupManifest.FileName);
            if (!File.Exists(manifestPath)) OutpostRestoreJournalCodec.WriteDurable(manifestPath, bytes);
            if (!File.ReadAllBytes(manifestPath).AsSpan().SequenceEqual(bytes)) throw new InvalidDataException("restore-source-manifest-changed");
            await ValidateRetainedCutAsync(sourceCut, plan.Cut, null, ct);
            await using var database = OpenRestoreDatabase(selected.DataDirectory);
            await database.Database.OpenConnectionAsync(ct);
            // VACUUM INTO produces a standalone DELETE-mode cut; install the required domain mode only under this durable restore intent.
            await database.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", ct);
            await database.Database.ExecuteSqlRawAsync("PRAGMA synchronous=FULL;", ct);
            OutpostSqliteBackend.Validate((SqliteConnection)database.Database.GetDbConnection(), OutpostDatabaseRole.Domain);
            var existing = await database.OutpostTransitions.AsNoTracking().SingleOrDefaultAsync(t => t.Id == selected.RestoreId, ct);
            if (existing is null)
            {
                await using var transaction = await database.Database.BeginTransactionAsync(ct);
                var authority = await database.OutpostAuthority.SingleAsync(ct);
                if (authority.HeadId != plan.Cut.Authority.HeadId || !authority.HeadDigest.AsSpan().SequenceEqual(plan.Cut.Authority.HeadDigest)
                    || authority.InstallationId != plan.Cut.Authority.InstallationId || authority.RestoreBoundary != plan.Cut.Authority.RestoreBoundary
                    || !(await OutpostDomainDigest.ComputeAsync(database, ct)).Span.SequenceEqual(plan.Cut.DomainDigest))
                    throw new InvalidDataException("restore-main-cut-changed");
                var expected = await OutpostRestoreEvidence.CreatePlanAsync(database, intent, journal.IntentDigest, plan.Cut, ct);
                if (!OutpostRestoreEvidence.Same(expected with { NextHeadId = plan.NextHeadId }, plan)) throw new InvalidDataException("restore-main-source-map-changed");
                foreach (var map in plan.Outposts)
                {
                    var row = await database.Outposts.SingleAsync(o => o.Id == map.Before.Id, ct);
                    database.Entry(row).CurrentValues.SetValues(map.After);
                }
                foreach (var map in plan.Custody)
                {
                    var row = await database.PawnCustody.SingleAsync(o => o.Id == map.Before.Id, ct);
                    database.Entry(row).CurrentValues.SetValues(map.After);
                }
                database.OutpostRestoreAuthorizations.AddRange(plan.Parcels.Select(p =>
                    OutpostRestoreEvidence.Decode<OutpostRestoreAuthorization>(OutpostRestoreEvidence.Encode(p.Authorization))));
                byte[] rootBytes = OutpostRestoreEvidence.Encode(plan);
                database.OutpostLifecycleOperations.Add(new()
                {
                    Id = selected.RestoreId, WorldId = authority.WorldId, RestoreBoundary = selected.TargetBoundary, Kind = "BulkRestore",
                    Actor = "offline-restore", RequestId = selected.RestoreId, Intent = rootBytes, IntentDigest = SHA256.HashData(rootBytes),
                    Phase = "MainCommitted", Progress = OutpostRestoreEvidence.Encode(new RestoreMainProgress(1, selected.RestoreId, null)), CreatedAt = rootTemplate.CreatedAt
                });
                await database.SaveChangesAsync(ct);
                rootTemplate.Effects = (await OutpostDomainDigest.ComputeAsync(database, ct)).ToByteArray();
                rootTemplate.Digest = OutpostTransitionDigest.Compute(new(plan.Cut.Authority.HeadId, ByteString.CopyFrom(plan.Cut.Authority.HeadDigest)), rootTemplate).ToByteArray();
                database.OutpostTransitions.Add(rootTemplate);
                authority.InstallationId = selected.TargetInstallationId; authority.RestoreBoundary = selected.TargetBoundary;
                authority.HeadId = rootTemplate.NextHeadId; authority.HeadDigest = rootTemplate.Digest; authority.TransitionId = rootTemplate.Id;
                await database.SaveChangesAsync(ct);
                await transaction.CommitAsync(CancellationToken.None);
                Checkpoint?.Invoke("AfterMainDatabaseCommit");
                existing = rootTemplate;
            }
            if (!existing.Intent.AsSpan().SequenceEqual(OutpostRestoreEvidence.Encode(plan))) throw new InvalidDataException("restore-main-commit-unknown");
            journal = journal with { Phase = RestorePhase.MainCommitted, Revision = checked(journal.Revision + 1) };
            StoreJournal(journalRoot, journal);
            Checkpoint?.Invoke("AfterMainCommitted");
        }
        if (journal.Phase is not (RestorePhase.MainCommitted or RestorePhase.Completed)) throw new InvalidDataException("restore-main-phase-invalid");
        return await CompleteMainAsync(plan, journal, journalRoot, anchor, held, ct);
    }

    private static OutpostTransition RootTransition(RestoreMainPlan plan) => new()
    {
        Id = plan.Journal.Selection.RestoreId, WorldId = plan.Cut.Authority.WorldId, RestoreBoundary = plan.Journal.Selection.TargetBoundary,
        Kind = "BulkRestore", Actor = "offline-restore", OldHeadId = plan.Cut.Authority.HeadId, PredecessorId = plan.Cut.Authority.TransitionId,
        NextHeadId = plan.NextHeadId, Intent = OutpostRestoreEvidence.Encode(plan), CreatedAt = plan.Cut.CutUtc
    };

    private async Task<RestoreResult> CompleteMainAsync(RestoreMainPlan plan, RestoreJournal journal, string journalRoot,
        OutpostAnchorStore anchor, OutpostAnchorStore.OutpostAnchorLease held, CancellationToken ct)
    {
        var selected = plan.Journal.Selection;
        await using var database = OpenRestoreDatabase(selected.DataDirectory);
        await database.Database.OpenConnectionAsync(ct);
        await database.Database.ExecuteSqlRawAsync("PRAGMA synchronous=FULL;", ct);
        var files = new SaveFiles(DataDirectory.Existing(selected.DataDirectory));
        var authority = await database.OutpostAuthority.SingleAsync(ct);
        var marker = OutpostRestoreFence.Marker(plan.Journal, journal.IntentDigest);
        var currentPublication = new AnchorRecord(1, selected.TargetInstallationId, authority.WorldId, selected.TargetBoundary, new(authority.HeadId, ByteString.CopyFrom(authority.HeadDigest)));
        var observedExternal = anchor.ReadEnvelope(held);
        if (observedExternal != marker && observedExternal != new KnownAnchorEnvelope(currentPublication)) throw new InvalidDataException("restore-completion-marker-mismatch");
        await OutpostAuthority.ValidateStoredChainAsync(database, files, authority, new(authority.HeadId, ByteString.CopyFrom(authority.HeadDigest)), ct);
        await new OutpostSnapshotGuard(database, files).ValidateStoredEvidenceCoreAsync(ct);
        var operation = await database.OutpostLifecycleOperations.SingleAsync(o => o.Id == selected.RestoreId, ct);
        var progress = OutpostRestoreEvidence.Decode<RestoreMainProgress>(operation.Progress);
        var root = await database.OutpostTransitions.AsNoTracking().SingleAsync(t => t.Id == selected.RestoreId, ct);
        OutpostTransition? previousCompletion = progress.CompletionId is null ? null
            : await database.OutpostTransitions.AsNoTracking().SingleAsync(t => t.Id == progress.CompletionId, ct);
        if (observedExternal == marker)
        {
            // Every explicit continuation while fenced excludes its entire interruption gap; R and previous steps stay immutable.
            if (journal.Phase != RestorePhase.MainCommitted || authority.TransitionId != (previousCompletion?.Id ?? root.Id))
                throw new InvalidDataException("restore-completion-source-invalid");
            await using var transaction = await database.Database.BeginTransactionAsync(ct);
            var sample = clock.Sample(); string completionId = Guid.NewGuid().ToString("D");
            var maps = new List<RestoreCheckpointMap>();
            var sources = previousCompletion is null ? plan.Outposts.Select(m => m.After).ToArray()
                : OutpostRestoreEvidence.Decode<RestoreCompletionPlan>(previousCompletion.Intent).Checkpoints.Select(m => m.After).ToArray();
            foreach (var source in sources)
            {
                var row = await database.Outposts.SingleAsync(o => o.Id == source.Id, ct);
                if (!OutpostRestoreEvidence.Same(row, source)) throw new InvalidDataException("restore-completion-current-map-changed");
                var before = OutpostRestoreEvidence.Decode<OutpostRow>(OutpostRestoreEvidence.Encode(row));
                if (row.State != "Archived")
                {
                    var checkpoint = OutpostClock.Advance(new(row.CheckpointUtc, row.ClockProcessId, row.ClockTimestamp, row.ClockFrequency), sample);
                    row.CheckpointUtc = checkpoint.Utc; row.ClockProcessId = checkpoint.ProcessId;
                    row.ClockTimestamp = checkpoint.Timestamp; row.ClockFrequency = checkpoint.Frequency;
                }
                row.LastTransitionId = completionId;
                maps.Add(new(before, OutpostRestoreEvidence.Decode<OutpostRow>(OutpostRestoreEvidence.Encode(row))));
            }
            var completion = new OutpostTransition
            {
                Id = completionId, WorldId = root.WorldId, RestoreBoundary = root.RestoreBoundary, Kind = "RestoreCompletion", Actor = root.Actor,
                OldHeadId = authority.HeadId, PredecessorId = authority.TransitionId, NextHeadId = Guid.NewGuid().ToString("D"), CreatedAt = sample.Utc,
                Intent = OutpostRestoreEvidence.Encode(new RestoreCompletionPlan(1, root.Id, root.Digest, sample, maps.ToArray(), previousCompletion?.Id))
            };
            OutpostRestoreEvidence.ValidateCompletion(root, plan, completion, previousCompletion);
            operation.Phase = "Completed"; operation.Progress = OutpostRestoreEvidence.Encode(new RestoreMainProgress(1, root.Id, completion.Id)); operation.Result = completion.Intent;
            await database.SaveChangesAsync(ct);
            completion.Effects = (await OutpostDomainDigest.ComputeAsync(database, ct)).ToByteArray();
            completion.Digest = OutpostTransitionDigest.Compute(new(authority.HeadId, ByteString.CopyFrom(authority.HeadDigest)), completion).ToByteArray();
            database.OutpostTransitions.Add(completion); authority.HeadId = completion.NextHeadId; authority.HeadDigest = completion.Digest; authority.TransitionId = completion.Id;
            await database.SaveChangesAsync(ct); await transaction.CommitAsync(CancellationToken.None);
            Checkpoint?.Invoke("AfterCompletionCommitted");
        }
        else if (previousCompletion is null) throw new InvalidDataException("restore-completion-not-published");
        await OutpostAuthority.ValidateStoredChainAsync(database, files, authority, new(authority.HeadId, ByteString.CopyFrom(authority.HeadDigest)), ct);
        await new OutpostSnapshotGuard(database, files).ValidateStoredEvidenceCoreAsync(ct);
        var published = new AnchorRecord(1, selected.TargetInstallationId, authority.WorldId, selected.TargetBoundary, new(authority.HeadId, ByteString.CopyFrom(authority.HeadDigest)));
        var external = anchor.ReadEnvelope(held);
        if (external != new KnownAnchorEnvelope(published))
        {
            if (external != marker || journal.Phase == RestorePhase.Completed) throw new InvalidDataException("restore-publication-marker-mismatch");
            anchor.WriteDurable(held, published, CancellationToken.None);
        }
        Checkpoint?.Invoke("AfterRestorePublication");
        if (journal.Phase != RestorePhase.Completed)
        {
            journal = journal with { Phase = RestorePhase.Completed, Revision = checked(journal.Revision + 1) }; StoreJournal(journalRoot, journal);
        }
        // Later ordinary descendants may advance the external head; the completed restore outcome itself is stable.
        var completed = OutpostRestoreEvidence.Decode<RestoreMainProgress>(operation.Progress);
        var resultStep = await database.OutpostTransitions.AsNoTracking().SingleAsync(t => t.Id == completed.CompletionId, ct);
        return new(RestoreOutcome.Completed, selected.RestoreId, published.InstallationId, published.RestoreBoundary, resultStep.NextHeadId, resultStep.Digest, "");
    }

    internal static RimlinkDbContext OpenRestoreDatabase(string root, bool readOnly = false) => new(new DbContextOptionsBuilder<RimlinkDbContext>().UseSqlite(new SqliteConnectionStringBuilder
    { DataSource = Path.Combine(root, BackupArchive.DatabaseFile), Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite, Pooling = false }.ToString()).Options);

    internal static async Task ValidateRetainedCutAsync(string sourceRoot, OutpostBackupManifest cut, Func<RimlinkDbContext, Task>? inspect, CancellationToken ct)
    {
        RejectLinks(sourceRoot);
        byte[] manifest = ReadJournalBytes(Path.Combine(sourceRoot, OutpostBackupManifest.FileName));
        if (!manifest.AsSpan().SequenceEqual(cut.Encode())) throw new InvalidDataException("restore-retained-source-manifest-mismatch");
        await VerifyInventoryAsync(sourceRoot, cut.Files, ct);
        string copy = Path.Combine(Path.GetTempPath(), "rimlink-restore-source-check-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(copy);
        try
        {
            await CopyInventoryAsync(sourceRoot, copy, cut.Files, ct);
            await ValidateCutAsync(copy, cut, ct);
            if (inspect is not null) { await using var db = OpenRestoreDatabase(copy, readOnly: true); await inspect(db); }
        }
        finally { Directory.Delete(copy, recursive: true); }
        await VerifyInventoryAsync(sourceRoot, cut.Files, ct);
    }
}
