using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Rimlink.Server.Storage.Entities;
namespace Rimlink.Server.Application.Procurement;

public sealed class ProcurementRequestConfiguration : IEntityTypeConfiguration<ProcurementRequest>
{
    public void Configure(EntityTypeBuilder<ProcurementRequest> b)
    {
        b.ToTable("procurement_requests");
        b.HasKey(x => x.Id);
        b.Property(x => x.RequestId).HasMaxLength(64);
        b.Property(x => x.IntentHash).HasMaxLength(64);
        b.HasIndex(x => new { x.PlayerId, x.RequestId }).IsUnique();
        b.HasOne<ProcurementOrder>().WithMany().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Player>().WithMany().HasForeignKey(x => x.PlayerId).OnDelete(DeleteBehavior.Restrict);
    }
}
