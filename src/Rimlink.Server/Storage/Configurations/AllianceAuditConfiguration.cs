using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Rimlink.Server.Storage.Entities;

namespace Rimlink.Server.Storage.Configurations;

public sealed class AllianceAuditConfiguration : IEntityTypeConfiguration<AllianceAudit>
{
    public void Configure(EntityTypeBuilder<AllianceAudit> e)
    {
        e.ToTable("alliance_audit");
        e.HasKey(x => x.Id);
        e.HasIndex(x => new { x.AllianceId, x.Id });
    }
}
