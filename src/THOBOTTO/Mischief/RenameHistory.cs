using Microsoft.EntityFrameworkCore;

using THOBOTTO.Data;

namespace THOBOTTO.Mischief;

// RecentCount: renames of the target by others in place since the cutoff (they set the price).
// LastAt: the target's last rename of any kind, buy-backs and ones still asked of the owner included (it sets the cooldown).
// LastByOthersAt: when the last one by others was in place (buying the name back prices by it). Declined ones count for nothing.
public sealed record RenameHistory(int RecentCount, DateTimeOffset? LastAt, DateTimeOffset? LastByOthersAt)
{
    public static async Task<RenameHistory> ForTargetAsync(BotDbContext db, ulong guildId, ulong targetId, DateTimeOffset since)
    {
        var renames = db.Renames.Where(r => r.GuildId == guildId && r.TargetId == targetId && r.DeclinedAt == null);

        var recent = await renames.CountAsync(r => r.ActorId != targetId && r.DoneAt >= since);
        var last = await renames.MaxAsync(r => (DateTimeOffset?)r.CreatedAt);
        var lastByOthers = await renames.Where(r => r.ActorId != targetId).MaxAsync(r => r.DoneAt);

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
