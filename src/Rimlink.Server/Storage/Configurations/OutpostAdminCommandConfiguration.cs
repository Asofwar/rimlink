using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Rimlink.Server.Storage.Entities;

namespace Rimlink.Server.Storage.Configurations;

public sealed class OutpostAdminCommandConfiguration : IEntityTypeConfiguration<OutpostAdminCommandRecord>
{
    public void Configure(EntityTypeBuilder<OutpostAdminCommandRecord> e)
    {
        e.ToTable("outpost_admin_commands", table =>
        {
            table.HasCheckConstraint("CK_outpost_admin_ids", "length(Id) = 36 AND length(RequestId) = 36 AND length(TransitionId) = 36 AND length(NextHeadId) = 36");
            table.HasCheckConstraint("CK_outpost_admin_actor", "length(ActorKey) = 64 AND ActorKey NOT GLOB '*[^0-9a-f]*'");
            table.HasCheckConstraint("CK_outpost_admin_action", "length(Action) BETWEEN 1 AND 64");
            table.HasCheckConstraint("CK_outpost_admin_reason", "length(CAST(Reason AS BLOB)) BETWEEN 1 AND 1024");
            table.HasCheckConstraint("CK_outpost_admin_command", "length(Command) BETWEEN 1 AND 65536 AND length(CommandDigest) = 32");
            table.HasCheckConstraint("CK_outpost_admin_result", "length(Result) BETWEEN 1 AND 65536");
            table.HasCheckConstraint("CK_outpost_admin_audit", "AuditId > 0 AND length(AuditProjection) BETWEEN 1 AND 65536");
        });
        e.HasKey(x => x.Id);
        e.HasIndex(x => new { x.ActorKey, x.Action, x.RequestId }).IsUnique();
        e.HasIndex(x => x.TransitionId).IsUnique();
        e.HasIndex(x => x.NextHeadId).IsUnique();
        e.HasIndex(x => x.AuditId).IsUnique();
        e.Property(x => x.CreatedAt).HasConversion(value => value, value => DateTime.SpecifyKind(value, DateTimeKind.Utc));
    }
}
