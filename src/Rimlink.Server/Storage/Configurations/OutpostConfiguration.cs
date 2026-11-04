using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Rimlink.Server.Storage.Entities;

namespace Rimlink.Server.Storage.Configurations;

public sealed class OutpostConfiguration : IEntityTypeConfiguration<OutpostRow>
{
    public void Configure(EntityTypeBuilder<OutpostRow> e)
    {
        e.ToTable("outposts", table =>
        {
            table.HasCheckConstraint("CK_outpost_stock", "Stock >= 0 AND Stock <= 1000");
            table.HasCheckConstraint("CK_outpost_fraction_numerator", IntegerText("FractionNumerator", true));
            table.HasCheckConstraint("CK_outpost_fraction_denominator", IntegerText("FractionDenominator", false));
        });
        e.HasKey(x => x.Id);
        e.HasIndex(x => new { x.WorldId, x.PlayerId, x.SaveGeneration }).IsUnique().HasFilter("OccupiesSlot = 1");
        e.HasIndex(x => new { x.WorldId, x.Tile }).IsUnique().HasFilter("OccupiesSlot = 1");
        // SQLite does not retain DateTime.Kind; persisted production coordinates are always UTC.
        e.Property(x => x.CheckpointUtc).HasConversion(value => value, value => DateTime.SpecifyKind(value, DateTimeKind.Utc));
        e.Property(x => x.FractionNumerator).HasMaxLength(256);
        e.Property(x => x.FractionDenominator).HasMaxLength(256);
        e.Property(x => x.Revision).IsConcurrencyToken();
    }

    // Canonical unsigned integer text. 256 decimal digits comfortably covers exact binary32 rates.
    private static string IntegerText(string column, bool zeroAllowed) =>
        $"length({column}) BETWEEN 1 AND 256 AND {column} NOT GLOB '*[^0-9]*' AND " +
        (zeroAllowed ? $"({column} = '0' OR substr({column},1,1) BETWEEN '1' AND '9')" : $"substr({column},1,1) BETWEEN '1' AND '9'");
}

public sealed class OutpostAssignmentConfiguration : IEntityTypeConfiguration<OutpostAssignment>
{
    public void Configure(EntityTypeBuilder<OutpostAssignment> e)
    {
        e.ToTable("outpost_assignments"); e.HasKey(x => x.Id);
        e.HasIndex(x => x.PawnCustodyId).IsUnique().HasFilter("Active = 1");
        e.HasIndex(x => new { x.OutpostId, x.Active });
        e.HasIndex(x => x.PreparedOperationId);
    }
}

public sealed class PawnCustodyConfiguration : IEntityTypeConfiguration<PawnCustodyRecord>
{
    public void Configure(EntityTypeBuilder<PawnCustodyRecord> e)
    {
        e.ToTable("pawn_custody"); e.HasKey(x => x.Id);
        e.HasIndex(x => new { x.WorldId, x.PlayerId, x.SaveGeneration });
        e.Property(x => x.Revision).IsConcurrencyToken();
    }
}

public sealed class OutpostTransitionConfiguration : IEntityTypeConfiguration<OutpostTransition>
{
    public void Configure(EntityTypeBuilder<OutpostTransition> e)
    {
        e.ToTable("outpost_transitions"); e.HasKey(x => x.Id);
        e.HasIndex(x => x.NextHeadId).IsUnique();
        e.HasIndex(x => new { x.WorldId, x.PlayerId, x.SaveGeneration });
        e.HasIndex(x => x.PawnCustodyId);
        e.HasOne<OutpostTransition>().WithMany().HasForeignKey(x => x.PredecessorId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class OutpostOperationConfiguration : IEntityTypeConfiguration<OutpostOperation>
{
    public void Configure(EntityTypeBuilder<OutpostOperation> e)
    {
        e.ToTable("outpost_operations"); e.HasKey(x => x.Id);
        e.HasIndex(x => new { x.WorldId, x.PlayerId, x.SaveGeneration, x.RestoreBoundary, x.RequestId }).IsUnique();
        e.Property(x => x.RequestId).HasMaxLength(64);
        e.HasIndex(x => new { x.OutpostId, x.State });
    }
}

public sealed class OutpostReservationConfiguration : IEntityTypeConfiguration<OutpostReservation>
{
    public void Configure(EntityTypeBuilder<OutpostReservation> e)
    {
        e.ToTable("outpost_reservations"); e.HasKey(x => x.Id);
        e.HasIndex(x => new { x.WorldId, x.Tile }).IsUnique().HasFilter("Active = 1");
        e.HasIndex(x => new { x.WorldId, x.PlayerId, x.SaveGeneration }).IsUnique().HasFilter("Active = 1");
        e.HasIndex(x => x.OperationId).IsUnique();
    }
}

public sealed class OutpostParcelLinkConfiguration : IEntityTypeConfiguration<OutpostParcelLink>
{
    public void Configure(EntityTypeBuilder<OutpostParcelLink> e)
    {
        e.ToTable("outpost_parcel_links"); e.HasKey(x => x.ParcelId); e.Property(x => x.ParcelId).ValueGeneratedNever();
        e.HasIndex(x => x.BatchId); // One stable batch may own multiple bounded child parcels.
        e.HasIndex(x => new { x.WorldId, x.PlayerId, x.SaveGeneration });
    }
}

public sealed class OutpostSnapshotLinkConfiguration : IEntityTypeConfiguration<OutpostSnapshotLink>
{
    public void Configure(EntityTypeBuilder<OutpostSnapshotLink> e)
    {
        e.ToTable("outpost_snapshot_links");
        e.HasKey(x => new { x.WorldId, x.PlayerId, x.SaveGeneration, x.SnapshotId });
        e.HasIndex(x => x.TransitionId);
    }
}

public sealed class OutpostManifestConfiguration : IEntityTypeConfiguration<OutpostManifest>
{
    public void Configure(EntityTypeBuilder<OutpostManifest> e)
    {
        e.ToTable("outpost_manifests"); e.HasKey(x => x.Id);
        e.HasIndex(x => new { x.WorldId, x.Revision }).IsUnique();
        e.HasIndex(x => x.WorldId).IsUnique().HasFilter("Active = 1");
    }
}

public sealed class OutpostAuthorityConfiguration : IEntityTypeConfiguration<OutpostAuthority>
{
    public void Configure(EntityTypeBuilder<OutpostAuthority> e)
    {
        e.ToTable("outpost_authority", table => table.HasCheckConstraint("CK_outpost_authority_singleton", "Id = 1"));
        e.HasKey(x => x.Id); e.Property(x => x.Id).ValueGeneratedNever();
        e.Property(x => x.HeadId).IsConcurrencyToken();
    }
}

public sealed class OutpostRestoreAuthorizationConfiguration : IEntityTypeConfiguration<OutpostRestoreAuthorization>
{
    public void Configure(EntityTypeBuilder<OutpostRestoreAuthorization> e)
    {
        e.ToTable("outpost_restore_authorizations"); e.HasKey(x => x.Id);
        e.HasIndex(x => new { x.RestoreBoundary, x.ParcelId }).IsUnique();
    }
}

public sealed class OutpostDeliveryAttemptConfiguration : IEntityTypeConfiguration<OutpostDeliveryAttempt>
{
    public void Configure(EntityTypeBuilder<OutpostDeliveryAttempt> e)
    {
        e.ToTable("outpost_delivery_attempts"); e.HasKey(x => x.Id);
        e.HasIndex(x => new { x.WorldId, x.PlayerId, x.SaveGeneration, x.RestoreBoundary, x.RequestId }).IsUnique();
        e.HasIndex(x => new { x.RestoreBoundary, x.ParcelId }).IsUnique().HasFilter("Active = 1");
    }
}

public sealed class OutpostLifecycleOperationConfiguration : IEntityTypeConfiguration<OutpostLifecycleOperation>
{
    public void Configure(EntityTypeBuilder<OutpostLifecycleOperation> e)
    {
        e.ToTable("outpost_lifecycle_operations"); e.HasKey(x => x.Id);
        e.HasIndex(x => new { x.RestoreBoundary, x.Actor, x.RequestId }).IsUnique();
    }
}
