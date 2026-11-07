using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Rimlink.Server.Application.Outposts;

public partial class OutpostAnchorStore
{
    // Call only inside the actual A/O/S/Live/read-T observation. No payload/hash of the mutable anchor is included.
    internal byte[] InitializeQualificationDigest(OutpostSchemaObservation schema, OutpostSqliteIdentity domain, OutpostSqliteIdentity authorityBackend)
    {
        var storage = OutpostStorageProfile.Observe(dataRoot, options.Value.AnchorDirectory,
            options.Value.StorageContract, options.Value.QualifiedLinuxFileSystem);
        return OutpostInitializeQualification.Digest(new(options.Value.StorageContract, OutpostInitializeQualification.AdmissionRevision,
            storage.DataDirectory, Path.Combine(storage.DataDirectory, "rimlink.db"), storage.AnchorDirectory,
            Path.Combine(storage.AnchorDirectory, "authority.db"), storage.DataFileSystem, storage.AnchorFileSystem,
            1380732737, 1, 1, 1, Facts(authorityBackend), Facts(domain), new(schema.ProfileId, schema.Digest.ToByteArray())));
    }

    private static OutpostInitializeBackendFacts Facts(OutpostSqliteIdentity identity)
    {
        var managed = typeof(SqliteConnection).Assembly; var provider = typeof(SQLitePCL.raw).Assembly;
        return new(identity.NativeVersion, identity.SourceId, identity.Vfs, Convert.FromHexString(identity.NativeSha256),
            managed.FullName ?? throw new InvalidDataException("initialize-managed-identity-unavailable"),
            managed.GetName().Version?.ToString() ?? throw new InvalidDataException("initialize-managed-version-unavailable"),
            provider.FullName ?? throw new InvalidDataException("initialize-provider-identity-unavailable"), identity.ProviderVersion);
    }
}

public sealed partial class OutpostAuthority
{
    // The canonical initialize audit is an initialization fragment even when the protected receipt is missing.
    // Other legacy audits are not initialization evidence and do not enter the stable qualification digest.
    private async Task<bool> HasInitializationFootprintAsync(CancellationToken ct) =>
        await HasHistoryAsync(ct) || await db.AdminActions.AsNoTracking().AnyAsync(a => a.Action == "outposts.Initialize", ct);

    private async Task<OutpostAuthorityObservation> ReadUninitializedObservationHeldAsync(OutpostLease lease, OutpostSqliteIdentity anchorIdentity, CancellationToken ct)
    {
        lease.Require(this);
        // Schema admission precedes every versioned reader, including the empty-history decision.
        var schema = await OutpostDomainSchema.ValidateAsync(db, OutpostSchemaContext.CurrentRuntime, ct);
        if (await HasInitializationFootprintAsync(ct)) return UnavailableObservation();
        var worlds = await db.World.AsNoTracking().Take(2).ToArrayAsync(ct);
        if (worlds.Length != 1 || string.IsNullOrWhiteSpace(worlds[0].InfluenceInstanceId)) return UnavailableObservation();
        var connection = (SqliteConnection)db.Database.GetDbConnection();
        var transaction = db.Database.CurrentTransaction?.GetDbTransaction() as SqliteTransaction
            ?? throw new InvalidDataException("admin-inspection-read-transaction-required");
        var domainIdentity = OutpostSqliteBackend.ValidateReadOnlyIdentity(connection, transaction);
        var world = worlds[0];
        return new(OutpostAuthorityObservationKind.Uninitialized, Uninitialized: new(world.InfluenceInstanceId,
            OutpostInitializeQualification.WorldDigest(world, world.InfluenceInstanceId),
            anchor.InitializeQualificationDigest(schema, domainIdentity, anchorIdentity), new(schema.ProfileId, schema.Digest.ToByteArray())));
    }
}
