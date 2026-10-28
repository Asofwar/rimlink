using Microsoft.EntityFrameworkCore;
using Rimlink.Server.Storage.Entities;

namespace Rimlink.Server.Storage;

public sealed class RimlinkDbContext(DbContextOptions<RimlinkDbContext> options) : DbContext(options)
{
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<Player> Players => Set<Player>();
    public DbSet<Session> Sessions => Set<Session>();
    public DbSet<Ban> Bans => Set<Ban>();
    public DbSet<World> World => Set<World>();
    public DbSet<WorldObject> WorldObjects => Set<WorldObject>();
    public DbSet<ChatMessage> ChatMessages => Set<ChatMessage>();
    public DbSet<ModpackEntry> ModpackEntries => Set<ModpackEntry>();
    public DbSet<SaveVersion> SaveVersions => Set<SaveVersion>();
    public DbSet<AdminAction> AdminActions => Set<AdminAction>();

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
            e.HasIndex(p => p.Name);
        });
        model.Entity<Session>(e => e.ToTable("sessions"));
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
    }
}
