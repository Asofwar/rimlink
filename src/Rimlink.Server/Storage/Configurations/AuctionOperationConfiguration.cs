using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Rimlink.Server.Storage.Entities;

namespace Rimlink.Server.Storage.Configurations;

public sealed class AuctionOperationConfiguration : IEntityTypeConfiguration<AuctionOperation>
{
    public void Configure(EntityTypeBuilder<AuctionOperation> e)
    {
        e.ToTable("AuctionOperations");
        e.HasKey(a => new { a.PlayerId, a.RequestId });
        e.Property(a => a.RequestId).HasMaxLength(64);
        e.Property(a => a.IntentHash).HasMaxLength(64);
        e.HasOne<Player>().WithMany().HasForeignKey(a => a.PlayerId).OnDelete(DeleteBehavior.Restrict);
    }
}
