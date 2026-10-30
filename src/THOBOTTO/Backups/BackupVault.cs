using Microsoft.EntityFrameworkCore;

using NetCord.Gateway;

using THOBOTTO.Data;

namespace THOBOTTO.Backups;

// A backup kept by the bot: weekly ones, the one made before each restore, and ones kept by hand.
public sealed class StoredBackup
{
    public long Id { get; init; }

    public ulong GuildId { get; init; }

    public required string Reason { get; init; }

    public required string Json { get; init; }

    public int Size { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
}

public static class BackupReasons
{
    public const string Weekly = "weekly";
    public const string BeforeRestore = "before restore";
    public const string Kept = "kept";

    // How many of each a server keeps; older ones go.
    public static int Keep(string reason) => reason switch
    {
        Weekly => 8,
        BeforeRestore => 5,
        _ => 10,
    };
}

// Keeps backups in the database, and makes a weekly one of every server.
public sealed class BackupVault(GatewayClient gateway, BackupMaker maker, IDbContextFactory<BotDbContext> dbFactory, TimeProvider time, ILogger<BackupVault> logger)
    : BackgroundService
{
    private static readonly TimeSpan Week = TimeSpan.FromDays(7);

    public async Task<StoredBackup> KeepAsync(Guild guild, string reason)
    {
        var json = (await maker.ExportAsync(guild)).ToJson();
        await using var db = await dbFactory.CreateDbContextAsync();
        var kept = new StoredBackup { GuildId = guild.Id, Reason = reason, Json = json, Size = json.Length, CreatedAt = time.GetUtcNow() };
        db.StoredBackups.Add(kept);
        await db.SaveChangesAsync();
        var old = await db.StoredBackups.Where(b => b.GuildId == guild.Id && b.Reason == reason).OrderByDescending(b => b.CreatedAt).Skip(BackupReasons.Keep(reason)).Select(b => b.Id).ToListAsync();
        await db.StoredBackups.Where(b => old.Contains(b.Id)).ExecuteDeleteAsync();
        return kept;
    }

    // Newest first, without their contents.
    public async Task<IReadOnlyList<StoredBackup>> ListAsync(ulong guildId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.StoredBackups.AsNoTracking().Where(b => b.GuildId == guildId).OrderByDescending(b => b.CreatedAt)
            .Select(b => new StoredBackup { Id = b.Id, GuildId = b.GuildId, Reason = b.Reason, Json = "", Size = b.Size, CreatedAt = b.CreatedAt })
            .ToListAsync();
    }

    public async Task<StoredBackup?> FindAsync(ulong guildId, long id)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.StoredBackups.AsNoTracking().FirstOrDefaultAsync(b => b.GuildId == guildId && b.Id == id);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(6), time);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            foreach (var guild in gateway.Cache.Guilds.Values.ToList())
            {
                try
                {
                    await using var db = await dbFactory.CreateDbContextAsync(stoppingToken);
                    var since = time.GetUtcNow() - Week;
                    if (!await db.StoredBackups.AnyAsync(b => b.GuildId == guild.Id && b.Reason == BackupReasons.Weekly && b.CreatedAt > since, stoppingToken))
                        await KeepAsync(guild, BackupReasons.Weekly);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Backing up {GuildId} failed", guild.Id);
                }
            }
        }
    }
}
