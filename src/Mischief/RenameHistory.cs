using Microsoft.EntityFrameworkCore;

using THOBOTTO.Data;

namespace THOBOTTO.Mischief;

// RecentCount: renames of the target by others since the cutoff (they set the price).
// LastAt: the target's last rename of any kind, buy-backs included (it sets the cooldown).
public sealed record RenameHistory(int RecentCount, DateTimeOffset? LastAt, DateTimeOffset? LastByOthersAt)
{
    public static async Task<RenameHistory> ForTargetAsync(BotDbContext db, ulong guildId, ulong targetId, DateTimeOffset since)
    {
        var renames = db.Renames.Where(r => r.GuildId == guildId && r.TargetId == targetId);

        var recent = await renames.CountAsync(r => r.ActorId != targetId && r.CreatedAt >= since);
        var last = await renames.MaxAsync(r => (DateTimeOffset?)r.CreatedAt);
        var lastByOthers = await renames.Where(r => r.ActorId != targetId).MaxAsync(r => (DateTimeOffset?)r.CreatedAt);

        return new(recent, last, lastByOthers);
    }
}

public static class MischiefEffects
{
    public static Task<MischiefEffect?> ActiveAsync(BotDbContext db, ulong guildId, ulong targetId, string kind, DateTimeOffset now)
        => db.MischiefEffects
            .Where(e => e.GuildId == guildId && e.TargetId == targetId && e.Kind == kind && e.EndsAt > now)
            .OrderByDescending(e => e.EndsAt)
            .FirstOrDefaultAsync();
}
