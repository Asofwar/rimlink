using System.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Rimlink.Server.Application.Outposts;

public sealed partial class OutpostAuthority
{
    // Invoked only by startup composition, after the existing authority/lifecycle/restore admission gates.
    // This prepares physical SQLite observation, not authority or Product Qualified.
    internal async Task PrepareStartupObservationAsync(OutpostMainObservation resource, CancellationToken ct)
    {
        if (!File.Exists(anchor.DatabasePath)) return;
        using var a = await anchor.AcquireAsync(ct);
        using var s = await files.PerStoreLock.HoldAsync(ct);
        var state = await OpenHeldAsync(a, ct);
        if (state.State is not (OutpostStartupState.Ready or OutpostStartupState.NoActiveWorld))
        {
            // An existing, positively empty, provisioned NULL anchor is not a missing/unavailable anchor.
            // Preparation cannot establish NULL, provision a store or initialize protected history.
            if (state.State != OutpostStartupState.RequiresInitialization || await HasHistoryAsync(ct)
                || anchor.JournalRefusal() is not null || anchor.ReadEnvelope(a) is not null) return;
            await OutpostDomainSchema.ValidateAsync(db, OutpostSchemaContext.CurrentRuntime, ct);
        }
        if (db.Database.CurrentTransaction is not null || db.ChangeTracker.HasChanges())
            throw new InvalidDataException("main-observation-startup-clean-context-required");
        bool opened = db.Database.GetDbConnection().State != ConnectionState.Open;
        try
        {
            await OpenDurableDomainAsync(ct);
            await resource.PrepareAsync((SqliteConnection)db.Database.GetDbConnection(), ct);
        }
        finally { if (opened) await db.Database.CloseConnectionAsync(); }
    }
}
