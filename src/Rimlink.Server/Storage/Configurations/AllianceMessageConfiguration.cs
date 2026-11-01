using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Rimlink.Server.Storage.Entities;

namespace Rimlink.Server.Storage.Configurations;

public sealed class AllianceMessageConfiguration : IEntityTypeConfiguration<AllianceMessage>
{
    public void Configure(EntityTypeBuilder<AllianceMessage> e)
    {
        e.ToTable("alliance_messages");
        e.HasKey(x => x.Id);
        e.HasIndex(x => new { x.AllianceId, x.Id });
        e.Property(x => x.Text).HasMaxLength(500);
    }
}
