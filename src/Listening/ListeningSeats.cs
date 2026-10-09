using System.Collections.Concurrent;

namespace THOBOTTO.Listening;

// Which helpers sit listening in which server, so music can tell them apart and (when music comes first)
// ask one to stop listening before sending it to play.
public sealed class ListeningSeats
{
    private readonly ConcurrentDictionary<(ulong Guild, ulong Helper), byte> _listening = new();

    // Set by the listening side: stops that helper listening in that server.
    public Func<ulong, ulong, Task>? Release { get; set; }

    public bool IsListening(ulong guildId, ulong helperId) => _listening.ContainsKey((guildId, helperId));

    public void Sit(ulong guildId, ulong helperId) => _listening[(guildId, helperId)] = 0;

    public void Leave(ulong guildId, ulong helperId) => _listening.TryRemove((guildId, helperId), out _);

    public async Task FreeAsync(ulong guildId, ulong helperId)
    {
        if (IsListening(guildId, helperId) && Release is { } release)
            await release(guildId, helperId);
    }
}
