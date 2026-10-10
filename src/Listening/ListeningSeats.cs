using System.Collections.Concurrent;

using NetCord.Gateway.Voice;

using THOBOTTO.Helpers;

namespace THOBOTTO.Listening;

// Which helpers sit listening where, so music can tell them apart and (when music comes first) ask one to stop
// listening before sending it to play; through the relay, it plays where it listens on the same connection.
public sealed class ListeningSeats
{
    // (server, helper) → the voice channel it listens in.
    private readonly ConcurrentDictionary<(ulong Guild, ulong Helper), ulong> _listening = new();

    // The rest is set by the listening side. Stops that helper listening in that server.
    public Func<ulong, ulong, Task>? Release { get; set; }

    // Stops that helper listening in that server but keeps it connected: its connection, to play on.
    public Func<ulong, ulong, VoiceClient?>? HandOver { get; set; }

    // A helper done playing in a channel that still wants a listener stays there to listen (true).
    public Func<HelperBot, ulong, ulong, Task<bool>>? TakeBack { get; set; }

    public bool IsListening(ulong guildId, ulong helperId) => _listening.ContainsKey((guildId, helperId));

    public ulong? ListenerIn(ulong guildId, ulong channelId)
        => _listening.Where(l => l.Key.Guild == guildId && l.Value == channelId).Select(l => (ulong?)l.Key.Helper).FirstOrDefault();

    public void Sit(ulong guildId, ulong helperId, ulong channelId) => _listening[(guildId, helperId)] = channelId;

    public void Leave(ulong guildId, ulong helperId) => _listening.TryRemove((guildId, helperId), out _);

    public async Task FreeAsync(ulong guildId, ulong helperId)
    {
        if (IsListening(guildId, helperId) && Release is { } release)
            await release(guildId, helperId);
    }
}
