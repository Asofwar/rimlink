using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Rimlink.Server.Storage.Entities;
namespace Rimlink.Server.Application.Procurement;

public sealed class ProcurementDeliveryConfiguration : IEntityTypeConfiguration<ProcurementDelivery>
{
    public void Configure(EntityTypeBuilder<ProcurementDelivery> b)
    {
        b.ToTable("procurement_deliveries");
        b.HasKey(x => x.Id);
        b.HasIndex(x => x.OrderId);
        b.HasIndex(x => x.SupplierPlayerId);
        b.HasOne<ProcurementOrder>().WithMany().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Player>().WithMany().HasForeignKey(x => x.SupplierPlayerId).OnDelete(DeleteBehavior.Restrict);
    }
}
