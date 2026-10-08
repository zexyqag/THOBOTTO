using Microsoft.EntityFrameworkCore;

using THOBOTTO.GameServers;
using THOBOTTO.Voice;

namespace THOBOTTO.Data;

public sealed class BotDbContext(DbContextOptions<BotDbContext> options) : DbContext(options)
{
    public DbSet<EnabledModule> EnabledModules => Set<EnabledModule>();

    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();

    public DbSet<VoiceHub> VoiceHubs => Set<VoiceHub>();

    public DbSet<DynamicVoiceChannel> DynamicVoiceChannels => Set<DynamicVoiceChannel>();

    public DbSet<GameServer> GameServers => Set<GameServer>();

    public DbSet<ServerSettings> ServerSettings => Set<ServerSettings>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<EnabledModule>().HasKey(m => new { m.GuildId, m.Module });
        modelBuilder.Entity<AuditEntry>().HasIndex(e => new { e.GuildId, e.CreatedAt });

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
