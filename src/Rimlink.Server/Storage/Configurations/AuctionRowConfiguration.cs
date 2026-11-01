using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Rimlink.Server.Storage.Entities;

namespace Rimlink.Server.Storage.Configurations;

public sealed class AuctionRowConfiguration : IEntityTypeConfiguration<AuctionRow>
{
    public void Configure(EntityTypeBuilder<AuctionRow> e)
    {
        e.ToTable("Auctions", t => t.HasCheckConstraint("CK_Auctions_Money", "StartPrice > 0 AND BuyoutPrice >= 0 AND Bid >= 0"));
        e.HasKey(a => a.Id);
        e.HasIndex(a => new { a.Status, a.ExpiresAt });
        e.HasIndex(a => new { a.SellerPlayerId, a.Status });
        e.HasIndex(a => new { a.BidderPlayerId, a.Status });
        e.HasOne<Player>().WithMany().HasForeignKey(a => a.SellerPlayerId).OnDelete(DeleteBehavior.Restrict);
        e.HasOne<Player>().WithMany().HasForeignKey(a => a.BidderPlayerId).OnDelete(DeleteBehavior.Restrict);
        e.Property(a => a.CloseReason).HasMaxLength(256);
    }
}
