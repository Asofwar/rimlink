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
    public DbSet<Raid> Raids => Set<Raid>();
    public DbSet<RaidOutcome> RaidOutcomes => Set<RaidOutcome>();
    public DbSet<Visit> Visits => Set<Visit>();
    public DbSet<ShopItem> ShopItems => Set<ShopItem>();
    public DbSet<PendingRemoval> PendingRemovals => Set<PendingRemoval>();

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
        model.Entity<AdminAction>(e => e.ToTable("admin_actions"));

        model.Entity<Wallet>(e =>
        {
            e.ToTable("wallets", t => t.HasCheckConstraint("CK_wallets_balance", "\"Balance\" >= 0"));
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
