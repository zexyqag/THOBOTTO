using System.Collections.Concurrent;

using NetCord.Gateway;

namespace THOBOTTO.Discord;

public sealed record VoicePresenceEntry(ulong ChannelId, bool Deafened, bool IsBot, bool Streaming);

// Who is in which voice channel, recorded by the gateway handlers in event order.
// NetCord's own cache is updated only after handlers run, so features read this instead, and hear of each change
// once it's recorded.
public sealed class VoicePresence(ILogger<VoicePresence> logger)
{
    // Guild → user → presence.
    private readonly ConcurrentDictionary<ulong, ConcurrentDictionary<ulong, VoicePresenceEntry>> _guilds = new();

    // A member's voice state changed; with it, the channel they were in before. Subscribers run one after the
    // other, in the order they subscribed, each on its own: one failing stops neither the next nor the gateway.
    public event Func<VoiceState, ulong?, Task>? Changed;

    // The bot (re)joined a guild, and everyone in voice there is recorded afresh.
    public event Func<Guild, Task>? Seeded;

    public async Task SeedAsync(Guild guild)
    {
        _guilds[guild.Id] = new(guild.VoiceStates.Values
            .Where(v => v.ChannelId.HasValue)
            .ToDictionary(v => v.UserId, Entry));
        foreach (var subscriber in Seeded?.GetInvocationList().Cast<Func<Guild, Task>>() ?? [])
            await TellAsync(() => subscriber(guild));
    }

    public async Task RecordAsync(VoiceState state)
    {
        var users = _guilds.GetOrAdd(state.GuildId, _ => new());
        var before = users.GetValueOrDefault(state.UserId)?.ChannelId;
        if (state.ChannelId.HasValue)
            users[state.UserId] = Entry(state);
        else
            users.TryRemove(state.UserId, out _);
        foreach (var subscriber in Changed?.GetInvocationList().Cast<Func<VoiceState, ulong?, Task>>() ?? [])
            await TellAsync(() => subscriber(state, before));
    }

    public IReadOnlyDictionary<ulong, VoicePresenceEntry> Snapshot(ulong guildId)
        => _guilds.TryGetValue(guildId, out var users) ? users.ToDictionary() : new Dictionary<ulong, VoicePresenceEntry>();

    private async Task TellAsync(Func<Task> subscriber)
    {
        try
        {
            await subscriber();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "A voice presence subscriber failed");
        }
    }

    private static VoicePresenceEntry Entry(VoiceState v)
        => new(v.ChannelId!.Value, v.IsDeafened || v.IsSelfDeafened, v.User?.IsBot ?? false, v.SelfStreamExists == true);
}
