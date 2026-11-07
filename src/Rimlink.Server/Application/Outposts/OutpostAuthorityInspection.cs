using System.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Rimlink.Server.Application.Live;

namespace Rimlink.Server.Application.Outposts;

public sealed partial class OutpostAuthority
{
    internal Exception? LastInspectionFailure { get; private set; }
    private async Task<OutpostLease> AcquireReadOnlyAsync(IReadOnlyList<long> ownerIds, CancellationToken ct)
    {
        long[] ordered = ownerIds.Distinct().Order().ToArray();
        if (ordered.Any(id => id <= 0)) throw new InvalidDataException("admin-owner-set-invalid");
        var held = new List<IDisposable>();
        try
        {
            var authorityLease = await anchor.AcquireReadOnlyAsync(ct); held.Add(authorityLease);
            foreach (long id in ordered) held.Add(await owners.Hold(id, ct));
            held.Add(await files.PerStoreLock.HoldExistingAsync(ct));
            return new(this, ordered, authorityLease, held.ToArray());
        }
        catch
        {
            for (int index = held.Count - 1; index >= 0; index--) held[index].Dispose();
            throw;
        }
    }

    // Projection executes inside the same A/O/S/Live/read-T cut. It cannot turn a diagnostic Pending into a grant.
    internal Task<T> ReadAdminProjectionAsync<T>(OutpostMainObservation observationResource, LiveStateLock live,
        Func<OutpostAuthorityObservation, CancellationToken, Task<T>> project, CancellationToken ct) =>
        ReadAdminProjectionAsync(observationResource, live, (observed, _, token) => project(observed, token), ct);

    internal async Task<T> ReadAdminProjectionAsync<T>(OutpostMainObservation observationResource, LiveStateLock live,
        Func<OutpostAuthorityObservation, OutpostLease?, CancellationToken, Task<T>> project, CancellationToken ct)
    {
        LastInspectionFailure = null;
        bool projecting = false;
        SqliteConnection? main = null;
        string? originalConnectionString = null;
        OutpostMainObservation.Pin? observationPin = null;
        try
        {
            // Qualify both physical fences before opening the main SQLite file; absent fences are never created.
            anchor.ValidatePath();
            if (!File.Exists(anchor.DatabasePath) || !File.Exists(Path.Combine(files.DataRoot, "rimlink.db"))
                || !File.Exists(Path.Combine(files.DataRoot, "save-storage.lock")))
                throw new InvalidDataException("admin-inspection-fence-unavailable");
            if (db.Database.GetDbConnection() is not SqliteConnection connection || connection.State != ConnectionState.Closed
                || db.Database.CurrentTransaction is not null || db.ChangeTracker.HasChanges())
                throw new InvalidDataException("admin-inspection-clean-context-required");
            // Pin a lifecycle-prepared attachment BEFORE the first discovery query or native open.
            observationPin = observationResource.TryPin(Path.Combine(files.DataRoot, "rimlink.db")) ?? throw new InvalidDataException("admin-inspection-main-not-prepared");
            main = connection; originalConnectionString = connection.ConnectionString;
            connection.ConnectionString = observationPin.ReadOnlyConnectionString;
            long[] discovered;
            try
            {
                await OpenReadOnlyMainAsync(ct);
                discovered = await db.Players.AsNoTracking().OrderBy(p => p.Id).Select(p => p.Id).ToArrayAsync(ct);
            }
            finally { await db.Database.CloseConnectionAsync(); }
            await using var lease = await AcquireReadOnlyAsync(discovered, ct);
            using var liveHeld = await live.Hold();
            if (db.Database.CurrentTransaction is not null || db.ChangeTracker.HasChanges())
                throw new InvalidDataException("admin-inspection-clean-context-required");
            bool opened = db.Database.GetDbConnection().State == ConnectionState.Closed;
            try
            {
                if (opened) await OpenReadOnlyMainAsync(ct);
                using var readTransaction = connection.BeginTransaction(deferred: true);
                await using var borrowed = await db.Database.UseTransactionAsync(readTransaction, ct);
                var current = await db.Players.AsNoTracking().OrderBy(p => p.Id).Select(p => p.Id).ToArrayAsync(ct);
                if (!discovered.SequenceEqual(current)) throw new InvalidDataException("admin-owner-set-changed");
                OutpostAuthorityObservation observation;
                try { observation = await ReadAdminObservationHeldAsync(lease, ct); }
                catch (Exception error) when (InspectionError(error)) { LastInspectionFailure = error; observation = UnavailableObservation(); }
                projecting = true;
                return await project(observation, lease, ct);
            }
            finally { if (opened) await db.Database.CloseConnectionAsync(); }
        }
        catch (Exception error) when (!projecting && InspectionError(error))
        {
            LastInspectionFailure = error;
            return await project(UnavailableObservation(), null, ct);
        }
        finally
        {
            try
            {
                if (main is not null && originalConnectionString is not null)
                {
                    if (main.State != ConnectionState.Closed) await db.Database.CloseConnectionAsync();
                    main.ConnectionString = originalConnectionString;
                }
            }
            finally { observationPin?.Dispose(); }
        }
    }

    private async Task OpenReadOnlyMainAsync(CancellationToken ct)
    {
        await db.Database.OpenConnectionAsync(ct);
        _ = OutpostSqliteBackend.ValidateReadOnlyIdentity((SqliteConnection)db.Database.GetDbConnection());
    }

    private async Task<OutpostAuthorityObservation> ReadAdminObservationHeldAsync(OutpostLease lease, CancellationToken ct)
    {
        lease.Require(this);
        var anchorObservation = anchor.ReadReadOnlyObservation(lease.Anchor);
        var envelope = anchorObservation.Envelope;
        if (envelope is null) return await ReadUninitializedObservationHeldAsync(lease, anchorObservation.Backend, ct);
        if (envelope is not KnownAnchorEnvelope known || known.Record.Pending is not null || known.Record.Restore is not null)
            return UnavailableObservation();
        var record = known.Record;
        var row = await db.OutpostAuthority.AsNoTracking().SingleOrDefaultAsync(ct);
        if (row is null || row.Id != 1 || row.Phase != "Committed" || row.FormatVersion != record.FormatVersion
            || row.InstallationId != record.InstallationId || row.WorldId != record.WorldId || row.RestoreBoundary != record.RestoreBoundary
            || row.HeadId != record.Committed.Id || !row.HeadDigest.AsSpan().SequenceEqual(record.Committed.Digest.Span))
            return UnavailableObservation();
        var domain = await ReadStoredChainAsync(db, files, row, record.Committed, currentRuntime: true, ct);
        await new OutpostSnapshotGuard(db, files).ValidateStoredEvidenceCoreAsync(ct);
        feature.ObserveDomain(domain.Format, domain.Schema.IsCurrent);
        return new(OutpostAuthorityObservationKind.Committed, Committed: new(row.InstallationId, row.WorldId, row.RestoreBoundary,
            row.HeadId, (byte[])row.HeadDigest.Clone(), row.TransitionId!, domain.Format,
            new(domain.Schema.ProfileId, domain.Schema.Digest.ToByteArray())));
    }

    private static bool InspectionError(Exception error) => error is IOException or InvalidDataException or UnauthorizedAccessException
        or SqliteException or OutpostEvidenceUnavailableException;
    private static OutpostAuthorityObservation UnavailableObservation() =>
        new(OutpostAuthorityObservationKind.Unavailable, Unavailable: new("admin-inspection-evidence-unavailable"));
}
