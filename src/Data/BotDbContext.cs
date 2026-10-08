using Microsoft.EntityFrameworkCore;

using THOBOTTO.Access;
using THOBOTTO.Mischief;
using THOBOTTO.Modules;
using THOBOTTO.Points;
using THOBOTTO.GameServers;
using THOBOTTO.Voice;

namespace THOBOTTO.Data;

public sealed class BotDbContext(DbContextOptions<BotDbContext> options) : DbContext(options)
{
    public DbSet<EnabledModule> EnabledModules => Set<EnabledModule>();

    public DbSet<ModuleSettings> ModuleSettings => Set<ModuleSettings>();

    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();

    public DbSet<PermissionGrant> PermissionGrants => Set<PermissionGrant>();

    public DbSet<Rename> Renames => Set<Rename>();

    public DbSet<PointAccount> PointAccounts => Set<PointAccount>();

    public DbSet<PointEntry> PointEntries => Set<PointEntry>();

    public DbSet<VoiceHub> VoiceHubs => Set<VoiceHub>();

    public DbSet<DynamicVoiceChannel> DynamicVoiceChannels => Set<DynamicVoiceChannel>();

    public DbSet<GameServer> GameServers => Set<GameServer>();

    public DbSet<ServerSettings> ServerSettings => Set<ServerSettings>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<EnabledModule>().HasKey(m => new { m.GuildId, m.Module });
        modelBuilder.Entity<ModuleSettings>(e =>
        {
            e.HasKey(s => new { s.GuildId, s.Module });
            e.Property(s => s.Json).HasColumnType("jsonb");
        });
        modelBuilder.Entity<AuditEntry>().HasIndex(e => new { e.GuildId, e.CreatedAt });
        modelBuilder.Entity<PermissionGrant>().HasKey(g => new { g.GuildId, g.RoleId, g.Permission });
        modelBuilder.Entity<Rename>().HasIndex(r => new { r.GuildId, r.TargetId, r.CreatedAt });
        modelBuilder.Entity<PointAccount>().HasKey(a => new { a.GuildId, a.UserId });
        modelBuilder.Entity<PointEntry>().HasIndex(e => new { e.GuildId, e.UserId, e.CreatedAt });

        modelBuilder.Entity<VoiceHub>(e =>
        {
            e.HasKey(h => h.ChannelId);
            e.Property(h => h.ChannelId).ValueGeneratedNever();
            e.HasIndex(h => h.GuildId);
        });
        modelBuilder.Entity<DynamicVoiceChannel>(e =>
        {
            e.HasKey(c => c.ChannelId);
            e.Property(c => c.ChannelId).ValueGeneratedNever();
            e.HasIndex(c => c.GuildId);
        });
        modelBuilder.Entity<GameServer>(e =>
        {
            e.HasIndex(s => s.GuildId);
            e.Ignore(s => s.Address);
        });
        modelBuilder.Entity<ServerSettings>(e =>
        {
            e.HasKey(s => s.GuildId);
            e.Property(s => s.GuildId).ValueGeneratedNever();
        });
    }
}
