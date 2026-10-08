using Microsoft.EntityFrameworkCore;

using THOBOTTO.Data;

namespace THOBOTTO.Mischief;

public sealed record RenameHistory(int RecentCount, DateTimeOffset? LastAt)
{
    public static async Task<RenameHistory> ForTargetAsync(BotDbContext db, ulong guildId, ulong targetId, DateTimeOffset since)
    {
        var recent = await db.Renames
            .Where(r => r.GuildId == guildId && r.TargetId == targetId && r.CreatedAt >= since)
            .Select(r => r.CreatedAt)
            .ToListAsync();
        var last = await db.Renames
            .Where(r => r.GuildId == guildId && r.TargetId == targetId)
            .OrderByDescending(r => r.CreatedAt)
            .Select(r => (DateTimeOffset?)r.CreatedAt)
            .FirstOrDefaultAsync();

        return new(recent.Count, last);
    }
}
