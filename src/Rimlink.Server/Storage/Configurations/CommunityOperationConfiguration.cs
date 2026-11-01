using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Rimlink.Server.Storage.Entities;

namespace Rimlink.Server.Storage.Configurations;

public sealed class CommunityOperationConfiguration : IEntityTypeConfiguration<CommunityOperation>
{
    public void Configure(EntityTypeBuilder<CommunityOperation> e)
    {
        e.ToTable("community_operations");
        e.HasKey(x => new { x.PlayerId, x.RequestId });
        e.Property(x => x.RequestId).HasMaxLength(64);
    }
}
