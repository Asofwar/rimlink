using System.Data;
using System.Security.Cryptography;
using System.Text.Json;
using Google.Protobuf;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Rimlink.Server.Storage.Entities;
using Rimlink.Server.Application.Saves;
using Rimlink.Server.Application.World;
using Rimlink.Server.Storage;

namespace Rimlink.Server.Application.Outposts;

public sealed partial class OutpostAuthority(RimlinkDbContext db, OutpostAnchorStore anchor, SaveFiles files, ColonyLocks owners, OutpostFeature feature)
{
    internal Action<string>? CommitCheckpoint { get; set; }
    internal Exception? LastOpenFailure { get; private set; }
    internal OutpostStartupResult StartupObservation => feature.Startup;
    internal OutpostReadinessDecision ReadinessObservation => feature.Readiness;
    internal void RequireRuntimeCustody() => feature.RequireCustodyReadiness();
    internal void RequireRuntimeGuild(GuildCapture.GuildRuntimeUse use=GuildCapture.GuildRuntimeUse.Transport)
    {
        if (feature.VerifiedDomainFormat != OutpostDomainFormat.V3)
            throw new OutpostEvidenceUnavailableException("guild-domain-v3-required");
        feature.RequireGuildReadiness(use);
    }
    internal bool IsRuntimeComposition => feature.RuntimeComposition;
    internal bool CanActivateGuildFormat => feature.CanActivateGuildFormat;
    internal OutpostDomainFormat? VerifiedDomainFormat => feature.VerifiedDomainFormat;

    public async Task<OutpostLease> AcquireAsync(IReadOnlyList<long> ownerIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ownerIds);
        long[] ordered = ownerIds.Distinct().Order().ToArray();
        if (ordered.Any(id => id <= 0)) throw new ArgumentOutOfRangeException(nameof(ownerIds));
        var held = new List<IDisposable>();
        try
        {
            var authorityLease = await anchor.AcquireAsync(ct);
            held.Add(authorityLease);
            foreach (long id in ordered) held.Add(await owners.Hold(id, ct));
            held.Add(await files.PerStoreLock.HoldAsync(ct));
            return new(this, ordered, authorityLease, held.ToArray());
        }
        catch
        {
            for (int index = held.Count - 1; index >= 0; index--) held[index].Dispose();
            throw;
        }
    }

    /// <summary>Owns the only domain transaction; legacy stages can obtain generated source ids before final evidence is bound.</summary>
    public Task<OutpostDomainTransaction> BeginHeldAsync(OutpostLease lease, TransitionIntent intent, CancellationToken ct) =>
        BeginCoreAsync(lease, intent, null, ct);

    public async Task<OutpostDomainTransaction> BeginLifecycleHeldAsync(OutpostLease lease, TransitionIntent intent, LifecycleStep step, CancellationToken ct)
    {
        lease.Require(this);
        if (lease.LifecycleOperationId != step.OperationId || step.IntentDigest.Length != 32)
            throw new InvalidOperationException("lifecycle-owned-barrier-required");
        var operation = await db.OutpostLifecycleOperations.AsNoTracking().SingleOrDefaultAsync(r => r.Id == step.OperationId, ct);
        if (step.ExpectedPhase is null)
        {
            if (operation is not null || step.Cleanup
                || (intent.Kind is "WorldBind" or "Rollback" ? step.NextPhase != "Completed" : step.NextPhase != "Prepared"))
                throw new InvalidOperationException("lifecycle-step-mismatch");
        }
        else if (operation is null || operation.Phase != step.ExpectedPhase || !operation.IntentDigest.AsSpan().SequenceEqual(step.IntentDigest)
            || operation.Phase == "Completed" || step.Cleanup && (operation.Phase is not ("EvidenceRetained" or "Destructive") || intent.Kind != "CustodyTransfer"))
            throw new InvalidOperationException("lifecycle-step-mismatch");
        if (!step.Cleanup && intent.Kind is not ("Lifecycle" or "WorldBind" or "Rollback")) throw new InvalidOperationException("lifecycle-transition-required");
        return await BeginCoreAsync(lease, intent, operation is null ? null : step.OperationId, ct, intent.Kind == "WorldBind");
    }

    private async Task<OutpostDomainTransaction> BeginCoreAsync(OutpostLease lease, TransitionIntent intent, string? lifecycleOperation, CancellationToken ct, bool bindSuccessor = false, OutpostAdminIntent? admin = null,GuildCapture.GuildAcceptanceProvenance? guildBootstrap=null)
    {
        lease.Require(this);
        if (intent.PlayerId < 0 || (intent.PlayerId > 0 && !lease.CoversOwner(intent.PlayerId)))
            throw new InvalidOperationException("outpost-owner-lease-required");
        if (db.Database.CurrentTransaction is not null || db.ChangeTracker.HasChanges())
            throw new InvalidOperationException("outpost-clean-domain-context-required");
        var startup = await OpenHeldAsync(lease.Anchor, ct, lifecycleOperation);
        if (admin is null ? startup.State != (bindSuccessor ? OutpostStartupState.NoActiveWorld : OutpostStartupState.Ready)
            : startup.State is not (OutpostStartupState.Ready or OutpostStartupState.NoActiveWorld))
            throw new InvalidOperationException("outpost-authority-not-ready");
        AnchorRecord current = anchor.Read(lease.Anchor)!;
        if (current.Committed != intent.ExpectedHead || current.WorldId != intent.WorldId || current.RestoreBoundary != intent.RestoreBoundary)
            throw new InvalidOperationException("outpost-expected-head-mismatch");
        var transition = new OutpostTransition
        {
            Id = intent.Id, WorldId = intent.WorldId, PlayerId = intent.PlayerId, SaveGeneration = intent.SaveGeneration,
            RestoreBoundary = intent.RestoreBoundary, Kind = intent.Kind, Actor = intent.Actor,
            OldHeadId = current.Committed.Id, NextHeadId = admin?.Allocation.NextHeadId ?? Guid.NewGuid().ToString("D"),
            CreatedAt = admin?.Allocation.At ?? DateTime.UtcNow,
            PawnCustodyId = intent.PawnCustodyId, OutpostId = intent.OutpostId, Intent = intent.Intent.ToByteArray()
        };
        OutpostTransitionDigest.Compute(current.Committed, transition);
        bool opened = db.Database.GetDbConnection().State != ConnectionState.Open;
        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction? transaction = null;
        try
        {
            await OpenDurableDomainAsync(ct);
            transaction = await db.Database.BeginTransactionAsync(ct);
            var row = await db.OutpostAuthority.SingleAsync(ct);
            await db.Entry(row).ReloadAsync(ct);
            transition.PredecessorId = row.TransitionId;
            var domain = await ReadStoredChainAsync(db, files, row, current.Committed, currentRuntime: true, ct);
            var format = admin is null ? domain.Format : await ValidateAdminCommandHeldAsync(lease, admin, row, domain, ct);
            if (GuildCapture.GuildAuthorityEvidence.IsKind(intent.Kind) && format != OutpostDomainFormat.V3)
                throw new OutpostEvidenceUnavailableException("guild-domain-v3-required");
            if (bindSuccessor) feature.RequireSuccessorReadiness();
            else if (admin is null)
            {
                if(intent.Kind==GuildCapture.GuildAuthorityEvidence.AcceptanceBindingKind)
                {
                    if(guildBootstrap is null || feature.GuildCut is not{} bootstrapCut || guildBootstrap.WorldId!=row.WorldId || guildBootstrap.Boundary!=row.RestoreBoundary
                        || await db.Set<GuildCaptureOperation>().AnyAsync(ct))throw new OutpostEvidenceUnavailableException("guild-acceptance-bootstrap-refused");
                    GuildCapture.GuildRuntimeAdmission.Require(feature.Readiness,bootstrapCut with{Acceptance=guildBootstrap},feature.GuildGrant,GuildCapture.GuildRuntimeUse.Setup);
                }
                else if (GuildCapture.GuildAuthorityEvidence.IsKind(intent.Kind)) RequireRuntimeGuild(GuildCapture.GuildRuntimeAdmission.MutationUse(intent.Kind));
                else if (intent.Kind == "SnapshotAccepted")
                {
                    var metadata = new Protocol.OutpostRecoveryData { Metadata = Protocol.V1.OutpostSaveMetadata.Parser.ParseFrom(intent.Intent) };
                    if (metadata.Metadata.GuildMetadata is not null) RequireRuntimeGuild();
                    if (metadata.RequiresPersonalFamily || metadata.Metadata.GuildMetadata is null) RequireRuntimeCustody();
                }
                else if (intent.Kind is not ("StageManifest" or "ConfirmManifest") || await db.PawnCustody.AnyAsync(ct)) RequireRuntimeCustody();
            }
            return new(this, db, anchor, feature, lease, current, transition, row, transaction, opened, lease.LifecycleOperationId, format, domain.Acceptance);
        }
        catch
        {
            if (transaction is not null) await transaction.DisposeAsync();
            db.ChangeTracker.Clear();
            if (opened) await db.Database.CloseConnectionAsync();
            throw;
        }
    }

    public sealed class OutpostDomainTransaction : IAsyncDisposable
    {
        private readonly OutpostAuthority owner;
        private readonly RimlinkDbContext db;
        private readonly OutpostAnchorStore anchor;
        private readonly OutpostFeature feature;
        private readonly OutpostLease lease;
        private readonly AnchorRecord current;
        private readonly OutpostTransition transition;
        private readonly Storage.Entities.OutpostAuthority row;
        private readonly Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction;
        private readonly bool opened;
        private readonly string? lifecycleOperation;
        private readonly OutpostDomainFormat format;
        private readonly GuildCapture.GuildAcceptanceProvenance? acceptance;
        private bool attempted, completed, disposed;

        internal OutpostDomainTransaction(OutpostAuthority owner, RimlinkDbContext db, OutpostAnchorStore anchor, OutpostFeature feature,
            OutpostLease lease, AnchorRecord current, OutpostTransition transition, Storage.Entities.OutpostAuthority row,
            Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction, bool opened, string? lifecycleOperation, OutpostDomainFormat format,
            GuildCapture.GuildAcceptanceProvenance? acceptance)
        {
            this.owner = owner; this.db = db; this.anchor = anchor; this.feature = feature; this.lease = lease;
            this.current = current; this.transition = transition; this.row = row; this.transaction = transaction; this.opened = opened; this.lifecycleOperation = lifecycleOperation; this.format = format;
            this.acceptance = acceptance;
        }

        internal void RequireAdminStage(RimlinkDbContext context, OutpostLease held, string id, DateTime at, string kind)
        {
            lease.Require(owner);
            if (disposed || attempted || !ReferenceEquals(db, context) || !ReferenceEquals(lease, held)
                || !ReferenceEquals(db.Database.CurrentTransaction, transaction) || transition.Id != id
                || transition.CreatedAt != at || transition.Kind != kind)
                throw new InvalidOperationException("outpost-cancellation-requires-owned-transaction");
        }

        public async Task<AuthorityHead> CompleteAsync(ByteString finalIntent, CancellationToken ct)
        {
            lease.Require(owner);
            if (disposed || attempted) throw new InvalidOperationException("outpost-commit-already-attempted");
            attempted = true;
            var priorCut = feature.GuildCut;
            feature.GuildCut = null;
            bool pendingWritten = false;
            try
            {
                transition.Intent = finalIntent.ToByteArray();
                OutpostTransitionDigest.Compute(current.Committed, transition);
                await db.SaveChangesAsync(ct);
                string nextWorld = transition.Kind == "WorldBind"
                    ? await owner.ReadWorldBindTargetAsync(transition, ct) : current.WorldId;
                row.WorldId = nextWorld;
                await db.SaveChangesAsync(ct);
                if (await owner.UnsupportedEvidenceReasonAsync(ct, transition, lifecycleOperation) is { } evidenceReason)
                {
                    feature.Startup = new(OutpostStartupState.Maintenance, evidenceReason);
                    throw new OutpostEvidenceUnavailableException(evidenceReason);
                }
                transition.Effects = (await OutpostDomainDigest.ComputeAsync(db, format, ct)).ToByteArray();
                transition.Digest = OutpostTransitionDigest.Compute(current.Committed, transition).ToByteArray();
                var next = new AuthorityHead(transition.NextHeadId, ByteString.CopyFrom(transition.Digest));
                db.OutpostTransitions.Add(transition);
                row.HeadId = next.Id; row.HeadDigest = next.Digest.ToByteArray(); row.TransitionId = transition.Id;
                await db.SaveChangesAsync(ct);
                if (transition.Kind is "DomainFormatActivated" or "AdminManifestConfirmed" or "AdminResolved")
                    _ = await owner.ValidateAdminHeadAsync(row, next, ct);
                var acceptance = format == OutpostDomainFormat.V3
                    ? this.acceptance ?? await GuildCapture.GuildAuthorityEvidence.AcceptanceIdentityAsync(db, ct) : null;
                feature.RequireGuildProvenance(acceptance);
                var guildCut = format == OutpostDomainFormat.V3 && !await owner.IsNoActiveWorldAsync(ct)
                    ? new GuildVerifiedCut(row.WorldId, row.RestoreBoundary, next.Id, next.Digest,acceptance) : null;
                ct.ThrowIfCancellationRequested();
                owner.CommitCheckpoint?.Invoke("BeforePending");
                // Every write attempt may already be durable: from here cancellation cannot compensate.
                pendingWritten = true;
                anchor.WriteDurable(lease.Anchor, current with { Pending = new(transition.Id, next) }, ct);
                owner.CommitCheckpoint?.Invoke("AfterPending");
                await transaction.CommitAsync(CancellationToken.None);
                owner.CommitCheckpoint?.Invoke("AfterDomainCommit");
                anchor.WriteDurable(lease.Anchor, current with { WorldId = nextWorld, Committed = next }, CancellationToken.None);
                owner.CommitCheckpoint?.Invoke("AfterExternalCommit");
                feature.ObserveDomain(format, true);
                feature.GuildCut = guildCut;
                completed = true;
                return next;
            }
            catch (Exception error)
            {
                if (pendingWritten) feature.Startup = new(OutpostStartupState.Maintenance, "protected-commit-reconciliation-required");
                else if (error is OperationCanceledException && ct.IsCancellationRequested
                    && priorCut is not null && feature.Startup.State == OutpostStartupState.Ready)
                {
                    await transaction.RollbackAsync(CancellationToken.None);
                    feature.GuildCut = priorCut;
                }
                db.ChangeTracker.Clear();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (disposed) return;
            disposed = true;
            try { await transaction.DisposeAsync(); }
            finally
            {
                if (!completed) db.ChangeTracker.Clear();
                if (opened) await db.Database.CloseConnectionAsync();
            }
        }
    }

    // Stage neither owns another transaction nor publishes before the external authority commit.
    public async Task<AuthorityHead> CommitHeldAsync(OutpostLease lease, TransitionIntent intent,
        Func<RimlinkDbContext, CancellationToken, Task> stage, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(stage);
        await using var transaction = await BeginHeldAsync(lease, intent, ct);
        await stage(db, ct);
        return await transaction.CompleteAsync(intent.Intent, ct);
    }

    public async Task<OutpostStartupResult> InitializeAsync(AuthorityInitialization request, CancellationToken ct)
    {
        if (!request.Confirmed)
            return feature.Startup = new(OutpostStartupState.RequiresInitialization, "initialization-confirmation-required");
        using var authorityLease = await anchor.AcquireAsync(ct);
        using var storageLease = await files.PerStoreLock.HoldAsync(ct);
        if (await HasHistoryAsync(ct))
            return feature.Startup = new(OutpostStartupState.Maintenance, "initialization-history-not-empty");
        if (!File.Exists(anchor.DatabasePath))
            return feature.Startup = new(OutpostStartupState.RequiresInitialization, "physical-provisioning-required");
        if (anchor.ReadEnvelope(authorityLease) is not null)
            return feature.Startup = new(OutpostStartupState.Maintenance, "initialization-anchor-not-empty");
        if (string.IsNullOrWhiteSpace(request.Actor) || !await db.World.AsNoTracking().AnyAsync(w => w.InfluenceInstanceId == request.WorldId, ct))
            return feature.Startup = new(OutpostStartupState.Maintenance, "initialization-world-or-actor-invalid");
        bool opened = db.Database.GetDbConnection().State != ConnectionState.Open;
        bool pendingWritten = false;
        try
        {
            await OpenDurableDomainAsync(ct);
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            // Global A/storage exclude other protected writers; check persisted history again inside the transaction.
            if (await HasHistoryAsync(ct))
                return feature.Startup = new(OutpostStartupState.Maintenance, "initialization-history-not-empty");
            string installation = Guid.NewGuid().ToString("D");
            string boundary = Guid.NewGuid().ToString("D");
            var seed = new AnchorRecord(1, installation, request.WorldId, boundary, GenesisHead());
            var transition = new OutpostTransition
            {
                Id = Guid.NewGuid().ToString("D"), WorldId = request.WorldId, RestoreBoundary = boundary,
                Kind = "Initialize", Actor = request.Actor, OldHeadId = seed.Committed.Id,
                NextHeadId = Guid.NewGuid().ToString("D"), CreatedAt = DateTime.UtcNow,
                Intent = JsonSerializer.SerializeToUtf8Bytes(new { installation, boundary, world = request.WorldId }),
                Effects = (await OutpostDomainDigest.ComputeAsync(db, ct)).ToByteArray()
            };
            ByteString digest = OutpostTransitionDigest.Compute(seed.Committed, transition);
            transition.Digest = digest.ToByteArray();
            var next = new AuthorityHead(transition.NextHeadId, digest);
            db.OutpostTransitions.Add(transition);
            db.OutpostAuthority.Add(new Storage.Entities.OutpostAuthority
            {
                Id = 1, FormatVersion = 1, InstallationId = installation, WorldId = request.WorldId,
                RestoreBoundary = boundary, HeadId = next.Id, HeadDigest = next.Digest.ToByteArray(),
                TransitionId = transition.Id, Phase = "Committed"
            });
            await db.SaveChangesAsync(ct);
            ct.ThrowIfCancellationRequested();
            CommitCheckpoint?.Invoke("BeforePending");
            pendingWritten = true;
            anchor.WriteDurable(authorityLease, seed with { Pending = new(transition.Id, next) }, ct);
            CommitCheckpoint?.Invoke("AfterPending");
            // From the durable pending decision onward, a lost RPC must not cancel finalization or compensate.
            await transaction.CommitAsync(CancellationToken.None);
            CommitCheckpoint?.Invoke("AfterDomainCommit");
            anchor.WriteDurable(authorityLease, seed with { Committed = next }, CancellationToken.None);
            CommitCheckpoint?.Invoke("AfterExternalCommit");
            feature.ObserveDomain(OutpostDomainFormat.V1, true);
            return feature.Startup = new(OutpostStartupState.Ready);
        }
        catch
        {
            if (pendingWritten) feature.Startup = new(OutpostStartupState.Maintenance, "initialization-reconciliation-required");
            db.ChangeTracker.Clear();
            throw;
        }
        finally
        {
            if (opened) await db.Database.CloseConnectionAsync();
        }
    }

    public Task<OutpostStartupResult> CheckHeldAsync(OutpostLease lease, CancellationToken ct)
    {
        lease.Require(this);
        return OpenHeldAsync(lease.Anchor, ct);
    }

    public async Task<OutpostStartupResult> OpenAsync(CancellationToken ct)
    {
        LastOpenFailure = null;
        try
        {
            using var authorityLease = await anchor.AcquireAsync(ct);
            using var storageLease = await files.PerStoreLock.HoldAsync(ct);
            return await OpenHeldAsync(authorityLease, ct);
        }
        catch (Exception error) when (error is InvalidDataException or IOException or SqliteException)
        {
            LastOpenFailure = error;
            feature.GuildCut = null;
            return feature.Startup = new(OutpostStartupState.Maintenance, "anchor-storage-unavailable");
        }
    }

    private async Task<OutpostStartupResult> OpenHeldAsync(OutpostAnchorStore.OutpostAnchorLease authorityLease, CancellationToken ct, string? lifecycleOperation = null)
    {
        ct.ThrowIfCancellationRequested();
        var requestCancellation = ct;
        // Once transport admission is withdrawn, finish checking the evidence under the same storage lease.
        if (feature.GuildCut is not null) ct = CancellationToken.None;
        feature.GuildCut = null;
        if (!File.Exists(anchor.DatabasePath))
            return feature.Startup = await HasHistoryAsync(ct)
                ? new(OutpostStartupState.Maintenance, "anchor-missing-with-history")
                : new(OutpostStartupState.RequiresInitialization, "explicit-initialization-required");
        if (anchor.JournalRefusal() is { } refusal)
            return feature.Startup = new(OutpostStartupState.Maintenance, refusal);
        AnchorEnvelope? envelope = anchor.ReadEnvelope(authorityLease);
        if (envelope is null)
            return feature.Startup = await HasHistoryAsync(ct)
                ? new(OutpostStartupState.Maintenance, "anchor-uninitialized-with-history")
                : new(OutpostStartupState.RequiresInitialization, "explicit-initialization-required");
        if (envelope is not KnownAnchorEnvelope known)
            return feature.Startup = new(OutpostStartupState.Maintenance, "anchor-fresh-fence-requires-restore");
        AnchorRecord record = known.Record;
        if (record.Restore is not null)
            return feature.Startup = new(OutpostStartupState.Maintenance, "anchor-reconciliation-required");
        var row = await db.OutpostAuthority.AsNoTracking().SingleOrDefaultAsync(ct);
        AuthorityHead expected = record.Committed;
        bool pendingCommitted = false;
        if (record.Pending is { } pending && row is not null && row.HeadId == pending.NextHead.Id &&
            row.HeadDigest.AsSpan().SequenceEqual(pending.NextHead.Digest.Span) && row.TransitionId == pending.TransitionId)
        {
            expected = pending.NextHead;
            pendingCommitted = true;
        }
        bool pendingWorldBind = false;
        if (pendingCommitted && row!.WorldId != record.WorldId)
        {
            var binding = await db.OutpostTransitions.AsNoTracking().SingleOrDefaultAsync(t => t.Id == row.TransitionId, ct);
            pendingWorldBind = binding is { Kind: "WorldBind" } && binding.WorldId == record.WorldId;
        }
        if (row is null || row.Id != 1 || row.FormatVersion != record.FormatVersion || row.InstallationId != record.InstallationId ||
            row.WorldId != record.WorldId && !pendingWorldBind || row.RestoreBoundary != record.RestoreBoundary || row.HeadId != expected.Id ||
            !row.HeadDigest.AsSpan().SequenceEqual(expected.Digest.Span) || row.Phase != "Committed")
            return feature.Startup = new(OutpostStartupState.Maintenance, "authority-head-mismatch");
        bool opened = db.Database.GetDbConnection().State != ConnectionState.Open;
        try
        {
            await OpenDurableDomainAsync(ct);
            StoredDomainObservation domain;
            try { domain = await ReadStoredChainAsync(db, files, row, expected, currentRuntime: true, ct);feature.RequireGuildProvenance(domain.Acceptance); }
            catch (OutpostEvidenceUnavailableException error)
            { return feature.Startup = new(OutpostStartupState.Maintenance, error.Message); }
            if (record.Pending is { } uncommitted && !pendingCommitted &&
                await db.OutpostTransitions.AnyAsync(t => t.Id == uncommitted.TransitionId || t.NextHeadId == uncommitted.NextHead.Id, ct))
                return feature.Startup = new(OutpostStartupState.Maintenance, "authority-pending-evidence-conflict");
            // A matching digest cannot prove raw save bytes or the absence of missing evidence.
            if (await UnsupportedEvidenceReasonAsync(ct, null, lifecycleOperation) is { } evidenceReason)
                return feature.Startup = new(OutpostStartupState.Maintenance, evidenceReason);
            if (record.Pending is not null)
                anchor.WriteDurable(authorityLease, record with { WorldId = row.WorldId, Committed = expected, Pending = null }, CancellationToken.None);
            feature.ObserveDomain(domain.Format, domain.Schema.IsCurrent);
            feature.Startup = new(await IsNoActiveWorldAsync(ct)
                ? OutpostStartupState.NoActiveWorld : OutpostStartupState.Ready);
            if (feature.Startup.State == OutpostStartupState.Ready && domain.Format == OutpostDomainFormat.V3)
                feature.GuildCut = new(row.WorldId, row.RestoreBoundary, row.HeadId, ByteString.CopyFrom(row.HeadDigest),domain.Acceptance);
            requestCancellation.ThrowIfCancellationRequested();
            return feature.Startup;
        }
        finally
        {
            if (opened) await db.Database.CloseConnectionAsync();
        }
    }

    // Historical callers cannot grant a writer; runtime startup explicitly requires the migrated schema.
    internal static async Task ValidateStoredChainAsync(RimlinkDbContext db, SaveFiles files,
        Rimlink.Server.Storage.Entities.OutpostAuthority row, AuthorityHead expected, CancellationToken ct) =>
        _ = await ReadStoredChainAsync(db, files, row, expected, currentRuntime: false, ct);

    private Task<string> ReadWorldBindTargetAsync(OutpostTransition transition, CancellationToken ct) =>
        new OutpostLifecycleEvidence(db, files).ReadWorldBindTargetAsync(transition, ct);
    private Task<bool> IsNoActiveWorldAsync(CancellationToken ct) => new OutpostLifecycleEvidence(db, files).IsNoActiveWorldAsync(ct);

    private static AuthorityHead GenesisHead() => new(Guid.Empty.ToString("D"), ByteString.CopyFrom(SHA256.HashData("rimlink-outpost-genesis-v1"u8)));

    private async Task OpenDurableDomainAsync(CancellationToken ct)
    {
        if (db.Database.GetDbConnection().State != ConnectionState.Open)
            await db.Database.OpenConnectionAsync(ct);
        if (db.Database.GetDbConnection() is not SqliteConnection connection)
            throw new InvalidDataException("outpost-domain-provider-mismatch");
        // Configure this protected connection, not an unrelated pooled connection. Never rewrite its journal mode.
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA synchronous=FULL;";
        await command.ExecuteNonQueryAsync(ct);
        feature.LastAnchorBackend = anchor.LastBackendReport;
        feature.LastDomainBackend = OutpostSqliteBackend.Validate(connection, OutpostDatabaseRole.Domain);
    }

    private async Task<string?> UnsupportedEvidenceReasonAsync(CancellationToken ct, OutpostTransition? staged = null, string? lifecycleOperation = null)
    {
        try
        {
            await new OutpostSnapshotGuard(db, files).ValidateStoredEvidenceCoreAsync(ct, staged, lifecycleOperation);
            return null;
        }
        catch (OutpostEvidenceUnavailableException error) { return error.Message; }
    }

    internal async Task<bool> HasHistoryAsync(CancellationToken ct) =>
        await db.Outposts.AnyAsync(ct) || await db.OutpostAssignments.AnyAsync(ct) ||
        await db.PawnCustody.AnyAsync(ct) || await db.OutpostTransitions.AnyAsync(ct) ||
        await db.OutpostOperations.AnyAsync(ct) || await db.OutpostReservations.AnyAsync(ct) ||
        await db.OutpostParcelLinks.AnyAsync(ct) || await db.OutpostSnapshotLinks.AnyAsync(ct) ||
        await db.OutpostManifests.AnyAsync(ct) || await db.OutpostAuthority.AnyAsync(ct) ||
        await db.OutpostRestoreAuthorizations.AnyAsync(ct) || await db.OutpostDeliveryAttempts.AnyAsync(ct) ||
        await db.OutpostLifecycleOperations.AnyAsync(ct) || await db.OutpostOrdinaryReturns.AnyAsync(ct) || await HasAdminHistoryAsync(ct);

    private async Task<bool> HasAdminHistoryAsync(CancellationToken ct)
    {
        // Admit the complete actual catalog before querying the v2-only table; a historical pre-admin v1 cut has no such reader.
        var schema = await OutpostDomainSchema.ObserveAsync(db, ct);
        return schema.IsCurrent && await db.OutpostAdminCommands.AnyAsync(ct)
            || OutpostDomainSchema.HasGuildTables(schema) && await GuildCapture.GuildDomainDigest.HasRowsAsync(db, ct);
    }
}
