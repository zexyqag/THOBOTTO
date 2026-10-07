using Microsoft.EntityFrameworkCore;

namespace THOBOTTO.Data;

public sealed class BotDbContext(DbContextOptions<BotDbContext> options) : DbContext(options)
{
    public DbSet<EnabledModule> EnabledModules => Set<EnabledModule>();

    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<EnabledModule>().HasKey(m => new { m.GuildId, m.Module });
        modelBuilder.Entity<AuditEntry>().HasIndex(e => new { e.GuildId, e.CreatedAt });
    }
}
