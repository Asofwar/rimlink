using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Rimlink.Server.Storage.Entities;

namespace Rimlink.Server.Storage.Configurations;

public sealed class InfluenceOperationConfiguration : IEntityTypeConfiguration<InfluenceOperation>
{
    public void Configure(EntityTypeBuilder<InfluenceOperation> entity)
    {
        entity.ToTable("influence_operations");
        entity.HasKey(x => new { x.Actor, x.RequestId });
        entity.Property(x => x.Actor).HasMaxLength(160);
        entity.Property(x => x.RequestId).HasMaxLength(64);
    }
}
