using System.Collections.Concurrent;

using NetCord.Gateway;

namespace THOBOTTO.Voice;

public sealed record VoicePresenceEntry(ulong ChannelId, bool Deafened, bool IsBot);

// Who is in which voice channel, recorded by the gateway handlers in event order.
// NetCord's own cache is updated only after handlers run, so modules read this instead.
public sealed class VoicePresence
{
    // Guild → user → presence.
    private readonly ConcurrentDictionary<ulong, ConcurrentDictionary<ulong, VoicePresenceEntry>> _guilds = new();

    public void Seed(Guild guild)
    {
        _guilds[guild.Id] = new(guild.VoiceStates.Values
            .Where(v => v.ChannelId.HasValue)
            .ToDictionary(v => v.UserId, Entry));
    }

    public void Record(VoiceState state)
    {
        var users = _guilds.GetOrAdd(state.GuildId, _ => new());
        if (state.ChannelId.HasValue)
            users[state.UserId] = Entry(state);
        else
            users.TryRemove(state.UserId, out _);
    }

    public IReadOnlyDictionary<ulong, VoicePresenceEntry> Snapshot(ulong guildId)
        => _guilds.TryGetValue(guildId, out var users) ? users.ToDictionary() : new Dictionary<ulong, VoicePresenceEntry>();

    private static VoicePresenceEntry Entry(VoiceState v)
        => new(v.ChannelId!.Value, v.IsDeafened || v.IsSelfDeafened, v.User?.IsBot ?? false);
}
