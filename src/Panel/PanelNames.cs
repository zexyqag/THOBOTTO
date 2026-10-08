using System.Collections.Concurrent;

using NetCord.Rest;

namespace THOBOTTO.Panel;

// Names for the ids in cases and the audit log: the member's server nickname where they're in the
// server, else their Discord name. Remembered a while, to spare Discord.
public sealed class PanelNames(RestClient rest, TimeProvider time)
{
    private static readonly TimeSpan Remember = TimeSpan.FromMinutes(10);

    private readonly ConcurrentDictionary<(ulong Guild, ulong User), (string Name, DateTimeOffset At)> _names = new();

    public async Task<IReadOnlyDictionary<ulong, string>> NamesAsync(ulong guildId, IEnumerable<ulong> userIds)
    {
        var names = new Dictionary<ulong, string>();
        foreach (var id in userIds.Distinct())
            names[id] = await NameAsync(guildId, id);
        return names;
    }

    private async Task<string> NameAsync(ulong guildId, ulong userId)
    {
        var now = time.GetUtcNow();
        if (_names.TryGetValue((guildId, userId), out var known) && now - known.At < Remember)
            return known.Name;

        string name;
        try
        {
            var member = await rest.GetGuildUserAsync(guildId, userId);
            name = member.Nickname ?? member.GlobalName ?? member.Username;
        }
        catch (RestException)
        {
            try
            {
                var user = await rest.GetUserAsync(userId);
                name = user.GlobalName ?? user.Username;
            }
            catch (RestException)
            {
                name = userId.ToString();
            }
        }
        _names[(guildId, userId)] = (name, now);
        return name;
    }
}
