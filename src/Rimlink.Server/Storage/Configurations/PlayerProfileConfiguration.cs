using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Rimlink.Server.Storage.Entities;

namespace Rimlink.Server.Storage.Configurations;

public sealed class PlayerProfileConfiguration : IEntityTypeConfiguration<PlayerProfile>
{
    public void Configure(EntityTypeBuilder<PlayerProfile> e)
    {
        e.ToTable("player_profiles");
        e.HasKey(x => x.PlayerId);
        e.Property(x => x.PlayerId).ValueGeneratedNever();
        e.Property(x => x.Bio).HasMaxLength(500);
        e.HasOne<Player>().WithMany().HasForeignKey(x => x.PlayerId).OnDelete(DeleteBehavior.Restrict);
    }
}
