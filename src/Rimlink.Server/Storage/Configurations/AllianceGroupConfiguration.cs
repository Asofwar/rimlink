using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Rimlink.Server.Storage.Entities;

namespace Rimlink.Server.Storage.Configurations;

public sealed class AllianceGroupConfiguration : IEntityTypeConfiguration<AllianceGroup>
{
    public void Configure(EntityTypeBuilder<AllianceGroup> e)
    {
        e.ToTable("alliance_groups", t => t.HasCheckConstraint("CK_alliance_silver", "\"Silver\" >= 0"));
        e.HasKey(x => x.Id);
        e.Property(x => x.Name).HasMaxLength(32);
        e.HasOne<Player>().WithMany().HasForeignKey(x => x.OwnerId).OnDelete(DeleteBehavior.Restrict);
    }
}
