using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Rimlink.Server.Storage.Entities;
namespace Rimlink.Server.Application.Procurement;

public sealed class ProcurementOrderConfiguration : IEntityTypeConfiguration<ProcurementOrder>
{
    public void Configure(EntityTypeBuilder<ProcurementOrder> b)
    {
        b.ToTable("procurement_orders", t => {
            t.HasCheckConstraint("CK_procurement_units", "Quantity > 0 AND Quantity <= 1000000 AND Supplied >= 0 AND Supplied <= Quantity");
            t.HasCheckConstraint("CK_procurement_escrow", "Reward > 0 AND Escrow >= 0 AND Escrow <= 1000000000");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.DefName).HasMaxLength(128);
        b.HasIndex(x => new { x.Status, x.ExpiresAt });
        b.HasIndex(x => x.BuyerPlayerId);
        b.HasOne<Player>().WithMany().HasForeignKey(x => x.BuyerPlayerId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Player>().WithMany().HasForeignKey(x => x.SupplierPlayerId).OnDelete(DeleteBehavior.Restrict);
    }
}
