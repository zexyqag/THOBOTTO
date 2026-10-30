namespace THOBOTTO.Points;

// A member's free goes at something per day (UTC), kept in memory: a restart starts the day afresh, generous
// rather than strict.
public sealed class DailyAllowance
{
    private readonly Dictionary<(ulong GuildId, ulong UserId), (DateOnly Day, int Used)> _used = [];
    private readonly Lock _sync = new();

    // True, and one more used, while the member has had fewer than the free ones today.
    public bool Take(ulong guildId, ulong userId, DateTimeOffset now, int free)
    {
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        lock (_sync)
        {
            var (day, used) = _used.GetValueOrDefault((guildId, userId));
            if (day != today)
                used = 0;
            if (used >= free)
            {
                _used[(guildId, userId)] = (today, used);
                return false;
            }
            _used[(guildId, userId)] = (today, used + 1);
            return true;
        }
    }
}
