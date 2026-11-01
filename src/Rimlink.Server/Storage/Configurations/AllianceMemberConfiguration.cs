using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Rimlink.Server.Storage.Entities;

namespace Rimlink.Server.Storage.Configurations;

public sealed class AllianceMemberConfiguration : IEntityTypeConfiguration<AllianceMember>
{
    public void Configure(EntityTypeBuilder<AllianceMember> e)
    {
        e.ToTable("alliance_members");
        e.HasKey(x => x.PlayerId);
        e.HasIndex(x => x.AllianceId);
        e.HasOne<Player>().WithMany().HasForeignKey(x => x.PlayerId).OnDelete(DeleteBehavior.Restrict);
        e.HasOne<AllianceGroup>().WithMany().HasForeignKey(x => x.AllianceId).OnDelete(DeleteBehavior.Restrict);
    }
}
