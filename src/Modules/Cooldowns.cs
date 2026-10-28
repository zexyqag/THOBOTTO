namespace THOBOTTO.Modules;

// When each member of a server last did something that has a wait between goes.
public sealed class Cooldowns
{
    private readonly Dictionary<(ulong GuildId, ulong UserId), DateTimeOffset> _last = [];
    private readonly Lock _sync = new();

    // How much longer the member has to wait; zero when it's time.
    public TimeSpan Left(ulong guildId, ulong userId, DateTimeOffset now, TimeSpan between)
    {
        lock (_sync)
            return _last.TryGetValue((guildId, userId), out var last) && now - last < between ? between - (now - last) : TimeSpan.Zero;
    }

    public void Mark(ulong guildId, ulong userId, DateTimeOffset now)
    {
        lock (_sync)
            _last[(guildId, userId)] = now;
    }
}
