using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Rimlink.Server.Storage.Entities;

namespace Rimlink.Server.Storage.Configurations;

public sealed class CommunityInvitationConfiguration : IEntityTypeConfiguration<CommunityInvitation>
{
    public void Configure(EntityTypeBuilder<CommunityInvitation> e)
    {
        e.ToTable("community_invitations");
        e.HasKey(x => x.Id);
        e.HasIndex(x => new { x.ToId, x.State });
        e.HasIndex(x => new { x.FromId, x.State });
        e.HasOne<Player>().WithMany().HasForeignKey(x => x.FromId).OnDelete(DeleteBehavior.Restrict);
        e.HasOne<Player>().WithMany().HasForeignKey(x => x.ToId).OnDelete(DeleteBehavior.Restrict);
    }
}
