using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Rimlink.Server.Storage.Entities;

namespace Rimlink.Server.Storage.Configurations;

internal static class GuildCaptureConfiguration
{
    internal static void Configure(ModelBuilder model)
    {
        model.Entity<GuildOutpostRow>(e =>
        {
            e.ToTable("guild_outposts", t =>
            {
                t.HasCheckConstraint("CK_guild_stock", "Stock BETWEEN 0 AND 1000 AND Remainder >= 0 AND Remainder < 36000000000 AND (Stock < 1000 OR Remainder = 0)");
                t.HasCheckConstraint("CK_guild_resource", "Resource IN (1,2)");
                t.HasCheckConstraint("CK_guild_window", "DefenceHourUtc BETWEEN 0 AND 23 AND (PendingDefenceHourUtc IS NULL OR PendingDefenceHourUtc BETWEEN 0 AND 23)");
                t.HasCheckConstraint("CK_guild_outpost_bounds", "Tile >= 0 AND Revision >= 0 AND ConsecutiveAborts >= 0");
            });
            e.HasKey(x => x.Id); e.HasIndex(x => new { x.WorldId, x.Tile }).IsUnique();
            e.HasIndex(x => new { x.WorldId, x.AllianceId }); e.Property(x => x.Revision).IsConcurrencyToken();
            e.HasOne<AllianceGroup>().WithMany().HasForeignKey(x => x.AllianceId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<GuildCaptureConsent>(e =>
        {
            e.ToTable("guild_capture_consents"); e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.OutpostId, x.OwnerRevision }).IsUnique();
            e.HasOne<GuildOutpostRow>().WithMany().HasForeignKey(x => x.OutpostId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<GuildResourceObligation>(e =>
        {
            e.ToTable("guild_resource_obligations", t =>
                t.HasCheckConstraint("CK_guild_obligation", "Resource IN (1,2) AND OriginalCount BETWEEN 1 AND 1000 AND RemainingCount BETWEEN 0 AND OriginalCount AND ReservedCount BETWEEN 0 AND RemainingCount"));
            e.HasKey(x => x.Id); e.HasIndex(x => new { x.OutpostId, x.OwnerRevision }).IsUnique();
            e.HasIndex(x => new { x.AllianceId, x.RemainingCount });
            e.HasOne<GuildOutpostRow>().WithMany().HasForeignKey(x => x.OutpostId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<AllianceGroup>().WithMany().HasForeignKey(x => x.AllianceId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<GuildResourceClaim>(e =>
        {
            e.ToTable("guild_resource_claims", t =>
                t.HasCheckConstraint("CK_guild_claim", "Resource IN (1,2) AND Count BETWEEN 1 AND 1000 AND RecipientId > 0 AND SaveGeneration >= 0 AND SaveWriteEpoch >= 0"));
            e.HasKey(x => x.Id); e.HasIndex(x => new { x.ActorId, x.RequestId }).IsUnique();
            e.HasIndex(x => new { x.OriginKind, x.OriginId });
            e.HasOne<AllianceGroup>().WithMany().HasForeignKey(x => x.AllianceId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Player>().WithMany().HasForeignKey(x => x.RecipientId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<GuildParcelLink>(e =>
        {
            e.ToTable("guild_parcel_links"); e.HasKey(x => x.ParcelId); e.HasIndex(x => x.ClaimId).IsUnique();
            e.HasOne<Parcel>().WithMany().HasForeignKey(x => x.ParcelId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<GuildResourceClaim>().WithMany().HasForeignKey(x => x.ClaimId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<GuildDeliveryAttempt>(e =>
        {
            e.ToTable("guild_delivery_attempts"); e.HasKey(x => x.Id);
            e.HasIndex(x => x.ParcelId).IsUnique().HasFilter("Active = 1");
            e.HasIndex(x => new { x.RecipientId, x.WorldId, x.SaveGeneration, x.Boundary, x.RequestId }).IsUnique();
            e.HasOne<GuildParcelLink>().WithMany().HasForeignKey(x => x.ParcelId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<GuildCampaign>(e =>
        {
            e.ToTable("guild_campaigns", t => t.HasCheckConstraint("CK_guild_campaign_sides", "AttackerAllianceId > 0 AND DefenderAllianceId > 0 AND AttackerAllianceId <> DefenderAllianceId"));
            e.HasKey(x => x.Id); e.HasIndex(x => x.OutpostId).IsUnique().HasFilter("Active = 1");
            e.HasIndex(x => x.AttackerAllianceId).IsUnique().HasFilter("Active = 1");
            e.HasOne<GuildOutpostRow>().WithMany().HasForeignKey(x => x.OutpostId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<AllianceGroup>().WithMany().HasForeignKey(x => x.AttackerAllianceId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<AllianceGroup>().WithMany().HasForeignKey(x => x.DefenderAllianceId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<GuildAcquisitionReservation>(e =>
        {
            e.ToTable("guild_acquisition_reservations"); e.HasKey(x => x.Id);
            e.HasIndex(x => x.CampaignId).IsUnique(); e.HasIndex(x => new { x.AllianceId, x.Active });
            e.HasOne<GuildCampaign>().WithMany().HasForeignKey(x => x.CampaignId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<GuildBattleSlot>(e =>
        {
            e.ToTable("guild_battle_slots", t => t.HasCheckConstraint("CK_guild_slot_interval", "EndsAtUtc > StartsAtUtc"));
            e.HasKey(x => x.Id); e.HasIndex(x => x.CampaignId).IsUnique();
            e.HasIndex(x => new { x.ExecutorId, x.StartsAtUtc, x.Active });
            e.HasOne<GuildCampaign>().WithMany().HasForeignKey(x => x.CampaignId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<GuildBattle>(e =>
        {
            e.ToTable("guild_battles", t => t.HasCheckConstraint("CK_guild_battle_fence", "Attempt > 0 AND Fence > 0 AND ViewSequence >= 0"));
            e.HasKey(x => x.Id); e.HasIndex(x => new { x.CampaignId, x.Attempt }).IsUnique();
            e.HasIndex(x => x.CampaignId).IsUnique().HasFilter("Active = 1");
            e.HasIndex(x => x.ExecutorId).IsUnique().HasFilter("Active = 1");
            e.HasOne<GuildCampaign>().WithMany().HasForeignKey(x => x.CampaignId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<GuildBattleArtifact>(e =>
        {
            e.ToTable("guild_battle_artifacts", t => t.HasCheckConstraint("CK_guild_artifact", "SizeBytes BETWEEN 1 AND 67108864 AND length(Digest) = 32"));
            e.HasKey(x => x.Id); e.HasIndex(x => new { x.BattleId, x.Kind }).IsUnique();
            e.HasIndex(x => x.RelativePath).IsUnique();
            e.HasOne<GuildBattle>().WithMany().HasForeignKey(x => x.BattleId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<GuildBattleControlGrant>(e =>
        {
            e.ToTable("guild_battle_control_grants", t => t.HasCheckConstraint("CK_guild_control_grant", "Side IN (1,2) AND Epoch > 0 AND length(CapabilityDigest) = 32"));
            e.HasKey(x => x.Id); e.HasIndex(x => new { x.BattleId, x.Side }).IsUnique().HasFilter("Active = 1");
            e.HasIndex(x => new { x.BattleId, x.Side, x.Epoch }).IsUnique();
            e.HasOne<GuildBattle>().WithMany().HasForeignKey(x => x.BattleId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<GuildCaptureOperation>(e =>
        {
            e.ToTable("guild_capture_operations"); e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.ActorId, x.RequestId }).IsUnique();
        });
        var utc = new ValueConverter<DateTime, DateTime>(v => v, v => DateTime.SpecifyKind(v, DateTimeKind.Utc));
        foreach (var entity in model.Model.GetEntityTypes().Where(e => e.GetTableName()?.StartsWith("guild_", StringComparison.Ordinal) == true))
            foreach (var property in entity.GetProperties().Where(p => p.ClrType == typeof(DateTime) || p.ClrType == typeof(DateTime?)))
                property.SetValueConverter(utc);
    }
}
