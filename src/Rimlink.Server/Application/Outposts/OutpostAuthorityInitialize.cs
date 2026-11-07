using System.Data;
using System.Security.Cryptography;
using Google.Protobuf;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Rimlink.Server.Storage.Entities;

namespace Rimlink.Server.Application.Outposts;

public sealed partial class OutpostAuthority
{
    internal void RequireInitializationMain()
    {
        string expected = Path.GetFullPath(Path.Combine(files.DataRoot, "rimlink.db"));
        if (db.Database.GetDbConnection() is not SqliteConnection connection || !File.Exists(expected) || !File.Exists(anchor.DatabasePath)
            || !string.Equals(Path.GetFullPath(new SqliteConnectionStringBuilder(connection.ConnectionString).DataSource), expected,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) throw AdminMismatch();
    }

    internal async Task<(OutpostAdminCommitResult Result, AdminAction? Audit)> InitializeAdminHeldAsync(
        OutpostLease lease, OutpostAdminIntent command, OutpostMainObservation observation, CancellationToken ct)
    {
        lease.Require(this); RequireInitializationMain();
        if (command.Action != OutpostAdminActionKind.Initialize || db.Database.CurrentTransaction is not null || db.ChangeTracker.HasChanges())
            throw AdminMismatch();
        var selected = command.Initialize!; var expected = command.Expected.Uninitialized!;
        bool opened = db.Database.GetDbConnection().State != ConnectionState.Open;
        bool pendingDecision = false;
        try
        {
            // Confirmed mutation may use the ordinary qualified writer. GET never comes through this path.
            await OpenDurableDomainAsync(ct);
            var schema = await OutpostDomainSchema.ValidateAsync(db, OutpostSchemaContext.CurrentRuntime, ct);
            var envelope = anchor.ReadEnvelope(lease.Anchor);
            if (envelope is not null and not KnownAnchorEnvelope) throw AdminMismatch();
            var actual = (envelope as KnownAnchorEnvelope)?.Record;
            pendingDecision = actual?.Pending is not null;
            var row = await db.OutpostAuthority.AsNoTracking().SingleOrDefaultAsync(ct);
            if (row is not null)
            {
                var replay = await ReadInitializedReplayHeldAsync(lease, command, row, actual, ct);
                await observation.PrepareAsync((SqliteConnection)db.Database.GetDbConnection(), ct);
                feature.ObserveDomain(OutpostDomainFormat.V2, schema.IsCurrent);
                feature.Startup = new(OutpostStartupState.Ready);
                return (replay, null);
            }
            var seed = new AnchorRecord(1, selected.Installation, expected.World, selected.Boundary, GenesisHead());
            if (actual is not null && (actual.FormatVersion != 1 || actual.InstallationId != seed.InstallationId
                || actual.WorldId != seed.WorldId || actual.RestoreBoundary != seed.RestoreBoundary || actual.Committed != seed.Committed
                || actual.Restore is not null || actual.Pending is null || actual.Pending.TransitionId != command.Allocation.TransitionId
                || actual.Pending.NextHead.Id != command.Allocation.NextHeadId)) throw AdminMismatch();
            await ValidateUncommittedInitializeHeldAsync(lease, command, schema, ct);
            await observation.PrepareAsync((SqliteConnection)db.Database.GetDbConnection(), ct);
            var result = new OutpostAdminDurableResult(1, command.Allocation.ReceiptId,
                OutpostAdminDurableResultKind.AuthorityInitialized, null, null, [], true);
            var staged = OutpostAdminReceiptEvidence.Create(command, result);
            AuthorityHead next;
            await using (var transaction = await db.Database.BeginTransactionAsync(ct))
            {
                // Recheck the complete empty footprint, world, backend and allocation inside this sole SQL transaction.
                await ValidateUncommittedInitializeHeldAsync(lease, command, schema, ct);
                db.OutpostAdminCommands.Add(staged.Receipt); db.AdminActions.Add(staged.Audit);
                await db.SaveChangesAsync(ct);
                // Compute from the actual SQL representation, never an in-memory approximation of the staged receipt.
                var transition = new OutpostTransition
                {
                    Id = command.Allocation.TransitionId, WorldId = expected.World, RestoreBoundary = selected.Boundary,
                    Kind = "Initialize", Actor = staged.Receipt.ActorKey, OldHeadId = seed.Committed.Id,
                    NextHeadId = command.Allocation.NextHeadId, CreatedAt = command.Allocation.At,
                    Intent = OutpostAdminReceiptEvidence.Binding(staged.Receipt),
                    Effects = (await OutpostDomainDigest.ComputeAsync(db, OutpostDomainFormat.V2, ct)).ToByteArray()
                };
                var digest = OutpostTransitionDigest.Compute(seed.Committed, transition); transition.Digest = digest.ToByteArray();
                next = new(transition.NextHeadId, digest);
                var initialized = new Storage.Entities.OutpostAuthority
                {
                    Id = 1, FormatVersion = 1, InstallationId = selected.Installation, WorldId = expected.World,
                    RestoreBoundary = selected.Boundary, HeadId = next.Id, HeadDigest = next.Digest.ToByteArray(),
                    TransitionId = transition.Id, Phase = "Committed"
                };
                db.OutpostTransitions.Add(transition); db.OutpostAuthority.Add(initialized); await db.SaveChangesAsync(ct);
                _ = await ReadStoredChainAsync(db, files, initialized, next, currentRuntime: true, ct);
                if (await UnsupportedEvidenceReasonAsync(ct) is not null) throw AdminMismatch();
                var candidate = seed with { Pending = new(transition.Id, next) };
                // Exact original Genesis+Pending is the only reconstructible pre-DB decision. Never overwrite a mismatch.
                if (actual is not null && !AnchorRecordCodec.Encode(actual).AsSpan().SequenceEqual(AnchorRecordCodec.Encode(candidate))) throw AdminMismatch();
                ct.ThrowIfCancellationRequested(); CommitCheckpoint?.Invoke("BeforePending");
                if (actual is null)
                {
                    pendingDecision = true;
                    anchor.WriteDurable(lease.Anchor, candidate, ct);
                }
                CommitCheckpoint?.Invoke("AfterPending");
                await transaction.CommitAsync(CancellationToken.None);
                CommitCheckpoint?.Invoke("AfterDomainCommit");
                anchor.WriteDurable(lease.Anchor, seed with { Committed = next }, CancellationToken.None);
                CommitCheckpoint?.Invoke("AfterExternalCommit");
            }
            feature.ObserveDomain(OutpostDomainFormat.V2, schema.IsCurrent);
            feature.Startup = new(OutpostStartupState.Ready);
            return (new(result, next, false), staged.Audit);
        }
        catch
        {
            if (pendingDecision) feature.Startup = new(OutpostStartupState.Maintenance, "initialization-reconciliation-required");
            db.ChangeTracker.Clear(); throw;
        }
        finally { if (opened) await db.Database.CloseConnectionAsync(); }
    }

    private async Task ValidateUncommittedInitializeHeldAsync(OutpostLease lease, OutpostAdminIntent command,
        OutpostSchemaObservation admitted, CancellationToken ct)
    {
        var expected = command.Expected.Uninitialized!;
        var schema = await OutpostDomainSchema.ValidateAsync(db, OutpostSchemaContext.CurrentRuntime, ct);
        if (schema != admitted || await HasInitializationFootprintAsync(ct)
            || expected.Schema.ProfileId != schema.ProfileId || !expected.Schema.Digest.AsSpan().SequenceEqual(schema.Digest.Span)
            || await db.AdminActions.AnyAsync(a => a.Id == command.Allocation.AuditId, ct)) throw AdminMismatch();
        var worlds = await db.World.AsNoTracking().Take(2).ToArrayAsync(ct);
        if (worlds is not [var world] || world.InfluenceInstanceId != expected.World
            || !OutpostInitializeQualification.WorldDigest(world, world.InfluenceInstanceId).AsSpan().SequenceEqual(expected.WorldBindingDigest)) throw AdminMismatch();
        var anchorObservation = anchor.ReadReadOnlyObservation(lease.Anchor);
        var report = feature.LastDomainBackend ?? throw AdminMismatch(); // Actual qualified writer from OpenDurableDomainAsync.
        var domain = new OutpostSqliteIdentity(report.NativeVersion, report.SourceId, report.Vfs, report.NativeSha256, report.ProviderVersion);
        if (!anchor.InitializeQualificationDigest(schema, domain, anchorObservation.Backend).AsSpan().SequenceEqual(expected.QualificationDigest)) throw AdminMismatch();
    }

    private async Task<OutpostAdminCommitResult> ReadInitializedReplayHeldAsync(OutpostLease lease, OutpostAdminIntent command,
        Storage.Entities.OutpostAuthority row, AnchorRecord? actual, CancellationToken ct)
    {
        var selected = command.Initialize!; var expected = command.Expected.Uninitialized!;
        if (row.FormatVersion != 1 || row.Phase != "Committed" || actual is not { FormatVersion: 1, Restore: null }
            || row.InstallationId != selected.Installation || row.WorldId != expected.World || row.RestoreBoundary != selected.Boundary
            || actual.InstallationId != row.InstallationId || actual.WorldId != row.WorldId || actual.RestoreBoundary != row.RestoreBoundary) throw AdminMismatch();
        OutpostAdminCommitResult result;
        await using (var transaction = await db.Database.BeginTransactionAsync(ct))
        {
            var current = new AuthorityHead(row.HeadId, ByteString.CopyFrom(row.HeadDigest));
            var folded = await ReadStoredChainAsync(db, files, row, current, currentRuntime: true, ct);
            if (folded.Format != OutpostDomainFormat.V2 || await UnsupportedEvidenceReasonAsync(ct) is not null) throw AdminMismatch();
            string key = OutpostAdminIntentCodec.ActorKey(command.Actor);
            var receipt = await db.OutpostAdminCommands.AsNoTracking().SingleOrDefaultAsync(r => r.ActorKey == key
                && r.Action == "Initialize" && r.RequestId == command.RequestId, ct);
            if (receipt is null || !receipt.Command.AsSpan().SequenceEqual(OutpostAdminIntentCodec.Encode(command))) throw AdminMismatch();
            var initial = await db.OutpostTransitions.AsNoTracking().SingleAsync(t => t.Id == receipt.TransitionId, ct);
            var originalHead = new AuthorityHead(initial.NextHeadId, ByteString.CopyFrom(initial.Digest));
            if (actual.Pending is { } pending)
            {
                if (actual.Committed != GenesisHead() || pending.TransitionId != initial.Id || pending.NextHead != originalHead
                    || current != originalHead || row.TransitionId != initial.Id) throw AdminMismatch();
            }
            else if (actual.Committed != current) throw AdminMismatch();
            result = new(OutpostRestoreEvidence.Decode<OutpostAdminDurableResult>(receipt.Result), originalHead, true);
        }
        // No SQL effects on replay; an exact DB-committed initial Pending may finish only its verified external tuple.
        if (actual.Pending is not null)
        {
            anchor.WriteDurable(lease.Anchor, actual with { Committed = result.Head, Pending = null }, CancellationToken.None);
            CommitCheckpoint?.Invoke("AfterExternalCommit");
        }
        return result;
    }
}
