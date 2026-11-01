using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Rimlink.Server.Storage.Entities;

namespace Rimlink.Server.Storage.Configurations;

public sealed class FriendshipConfiguration : IEntityTypeConfiguration<Friendship>
{
    public void Configure(EntityTypeBuilder<Friendship> e)
    {
        e.ToTable("friendships");
        e.HasKey(x => new { x.FirstId, x.SecondId });
        e.HasOne<Player>().WithMany().HasForeignKey(x => x.FirstId).OnDelete(DeleteBehavior.Restrict);
        e.HasOne<Player>().WithMany().HasForeignKey(x => x.SecondId).OnDelete(DeleteBehavior.Restrict);
    }
}
