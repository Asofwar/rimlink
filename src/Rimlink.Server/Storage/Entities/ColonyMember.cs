using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Rimlink.Server.Storage.Entities;

public sealed class ColonyMember
{
    public long OwnerPlayerId { get; set; }
    public long MemberPlayerId { get; set; }
    public bool Accepted { get; set; }
    public string PawnIds { get; set; } = "";
}

public sealed class ColonyMemberConfiguration : IEntityTypeConfiguration<ColonyMember>
{
    public void Configure(EntityTypeBuilder<ColonyMember> e)
    {
        e.ToTable("colony_members");
        e.HasKey(x => new { x.OwnerPlayerId, x.MemberPlayerId });
        e.HasIndex(x => x.MemberPlayerId);
        e.HasOne<Player>().WithMany().HasForeignKey(x => x.OwnerPlayerId).OnDelete(DeleteBehavior.Cascade);
        e.HasOne<Player>().WithMany().HasForeignKey(x => x.MemberPlayerId).OnDelete(DeleteBehavior.Cascade);
    }
}
