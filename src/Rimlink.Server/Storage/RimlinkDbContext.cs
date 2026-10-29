using Microsoft.EntityFrameworkCore;
using Rimlink.Server.Storage.Entities;

namespace Rimlink.Server.Storage;

public sealed class RimlinkDbContext(DbContextOptions<RimlinkDbContext> options) : DbContext(options)
{
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<Player> Players => Set<Player>();
    public DbSet<SessionLog> Sessions => Set<SessionLog>();
    public DbSet<Ban> Bans => Set<Ban>();
    public DbSet<World> World => Set<World>();
    public DbSet<WorldObject> WorldObjects => Set<WorldObject>();
    public DbSet<ChatMessage> ChatMessages => Set<ChatMessage>();
    public DbSet<ModpackEntry> ModpackEntries => Set<ModpackEntry>();
    public DbSet<SaveVersion> SaveVersions => Set<SaveVersion>();
    public DbSet<AdminAction> AdminActions => Set<AdminAction>();
    public DbSet<Wallet> Wallets => Set<Wallet>();
    public DbSet<WalletEntry> WalletEntries => Set<WalletEntry>();
    public DbSet<Parcel> Parcels => Set<Parcel>();
    public DbSet<Lot> Lots => Set<Lot>();
    public DbSet<Transfer> Transfers => Set<Transfer>();
    public DbSet<ModpackDef> ModpackDefs => Set<ModpackDef>();
    public DbSet<ModpackConfig> ModpackConfigs => Set<ModpackConfig>();
    public DbSet<ForbiddenDef> ForbiddenDefs => Set<ForbiddenDef>();
    public DbSet<IncidentOrder> IncidentOrders => Set<IncidentOrder>();
    public DbSet<Raid> Raids => Set<Raid>();
    public DbSet<RaidOutcome> RaidOutcomes => Set<RaidOutcome>();
    public DbSet<Visit> Visits => Set<Visit>();
    public DbSet<LiveSession> LiveSessions => Set<LiveSession>();
    public DbSet<ShopItem> ShopItems => Set<ShopItem>();
    public DbSet<PendingRemoval> PendingRemovals => Set<PendingRemoval>();
    public DbSet<Trade> Trades => Set<Trade>();
    public DbSet<PlayerBlock> PlayerBlocks => Set<PlayerBlock>();
    public DbSet<ArchivedColony> ArchivedColonies => Set<ArchivedColony>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<Account>(e =>
        {
            e.ToTable("accounts");
            e.HasIndex(a => a.SteamId).IsUnique();
            e.HasIndex(a => a.Login).IsUnique();
            e.Property(a => a.Login).UseCollation("NOCASE");
            e.HasOne(a => a.Player).WithOne(p => p.Account).HasForeignKey<Player>(p => p.AccountId);
        });
        model.Entity<Player>(e =>
        {
            e.ToTable("players");
            e.HasIndex(p => p.AccountId).IsUnique();
            e.Property(p => p.Name).UseCollation("NOCASE");
            e.HasIndex(p => p.Name).IsUnique();
        });
        model.Entity<SessionLog>(e => e.ToTable("sessions"));
        model.Entity<Ban>(e =>
        {
            e.ToTable("bans");
            e.HasIndex(b => b.AccountId);
            e.HasIndex(b => b.SteamId);
            e.HasIndex(b => b.Ip);
        });
        model.Entity<World>(e => e.ToTable("world"));
        model.Entity<WorldObject>(e =>
        {
            e.ToTable("world_objects");
            e.HasOne(o => o.Owner).WithMany().HasForeignKey(o => o.OwnerPlayerId);
            e.HasIndex(o => o.OwnerPlayerId);
            // One settlement per tile; other object types may share tiles.
            e.HasIndex(o => o.Tile).IsUnique().HasFilter($"\"Type\" = '{WorldObjectTypes.Settlement}'");
        });
        model.Entity<ChatMessage>(e =>
        {
            e.ToTable("chat_messages");
            e.HasIndex(m => m.SentAt);
        });
        model.Entity<ModpackEntry>(e =>
        {
            e.ToTable("modpack_entries");
            e.HasIndex(m => m.PackageId).IsUnique();
        });
        model.Entity<SaveVersion>(e =>
        {
            e.ToTable("save_versions");
            e.HasIndex(s => new { s.PlayerId, s.Number }).IsUnique();
        });
        model.Entity<AdminAction>(e =>
        {
            e.ToTable("admin_actions");
            e.HasIndex(a => new { a.TargetAccountId, a.At });
        });
        model.Entity<ArchivedColony>(e =>
        {
            e.ToTable("archived_colonies");
            e.HasIndex(c => new { c.PlayerId, c.State });
        });

        model.Entity<Wallet>(e =>
        {
            // No balance check: storage fees may leave a debt (only WalletService.Charge goes below zero).
            e.ToTable("wallets");
            e.HasKey(w => w.PlayerId);
            e.Property(w => w.PlayerId).ValueGeneratedNever();
        });
        model.Entity<WalletEntry>(e =>
        {
            e.ToTable("wallet_entries");
            e.HasIndex(w => w.PlayerId);
        });
        model.Entity<Parcel>(e =>
        {
            e.ToTable("parcels");
            e.HasIndex(p => new { p.ToPlayerId, p.DeliveredAt });
            e.HasIndex(p => p.ClientRequestId).IsUnique().HasFilter("\"ClientRequestId\" IS NOT NULL");
        });
        model.Entity<Lot>(e =>
        {
            e.ToTable("lots");
            e.HasIndex(l => new { l.Status, l.ExpiresAt });
            e.HasIndex(l => l.SellerPlayerId);
            e.HasIndex(l => l.ClientRequestId).IsUnique().HasFilter("\"ClientRequestId\" IS NOT NULL");
        });
        model.Entity<Trade>(e =>
        {
            e.ToTable("trades");
            e.HasIndex(t => t.Status);
            e.HasIndex(t => t.InitiatorPlayerId);
            e.HasIndex(t => t.PartnerPlayerId);
            e.HasIndex(t => t.ClientRequestId).IsUnique().HasFilter("\"ClientRequestId\" IS NOT NULL");
        });
        model.Entity<PlayerBlock>(e =>
        {
            e.ToTable("player_blocks");
            e.HasIndex(b => new { b.BlockerPlayerId, b.BlockedPlayerId }).IsUnique();
            e.HasIndex(b => b.BlockedPlayerId);
        });
        model.Entity<Transfer>(e =>
        {
            e.ToTable("transfers");
            e.HasIndex(t => t.FromPlayerId);
            e.HasIndex(t => t.ClientRequestId).IsUnique().HasFilter("\"ClientRequestId\" IS NOT NULL");
        });
        model.Entity<ModpackDef>(e =>
        {
            e.ToTable("modpack_defs");
            e.HasKey(d => d.DefName);
        });
        model.Entity<ModpackConfig>(e =>
        {
            e.ToTable("modpack_configs");
            e.HasIndex(c => new { c.PackageId, c.Handle }).IsUnique();
        });
        model.Entity<ForbiddenDef>(e =>
        {
            e.ToTable("forbidden_defs");
            e.HasKey(d => d.DefName);
        });
        model.Entity<IncidentOrder>(e =>
        {
            e.ToTable("incidents");
            e.HasIndex(i => new { i.TargetPlayerId, i.State });
            e.HasIndex(i => i.BuyerPlayerId);
            e.HasIndex(i => i.ClientRequestId).IsUnique().HasFilter("\"ClientRequestId\" IS NOT NULL");
        });

        model.Entity<Raid>(e =>
        {
            e.ToTable("raids");
            e.HasIndex(r => new { r.TargetPlayerId, r.State });
            e.HasIndex(r => new { r.AttackerPlayerId, r.State });
            e.HasIndex(r => new { r.AttackerPlayerId, r.ClientRequestId }).IsUnique().HasFilter("\"ClientRequestId\" IS NOT NULL");
        });
        model.Entity<RaidOutcome>(e =>
        {
            e.ToTable("raid_outcomes");
            e.HasIndex(o => o.RaidId);
        });
        model.Entity<LiveSession>(e =>
        {
            e.ToTable("live_sessions");
            e.HasIndex(s => new { s.HostPlayerId, s.State });
            e.HasIndex(s => new { s.GuestPlayerId, s.State });
            e.HasIndex(s => new { s.GuestPlayerId, s.ClientRequestId }).IsUnique().HasFilter("\"ClientRequestId\" IS NOT NULL");
        });
        model.Entity<Visit>(e =>
        {
            e.ToTable("visits");
            e.HasIndex(v => new { v.VisitorPlayerId, v.EndedAt });
        });
        model.Entity<ShopItem>(e =>
        {
            e.ToTable("shop_items", t => t.HasCheckConstraint("CK_shop_items_count", "\"Count\" >= 0"));
            e.HasIndex(s => new { s.HostPlayerId, s.ThingId }).IsUnique();
        });
        model.Entity<PendingRemoval>(e =>
        {
            e.ToTable("pending_removals");
            e.HasIndex(r => new { r.HostPlayerId, r.AckedAt });
        });
    }
}
