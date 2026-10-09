using System.Collections.Concurrent;

namespace THOBOTTO.Listening;

// Which helper sits listening in which server. Music comes first: it picks a listening helper last, and
// asks it to stop listening before sending it to play.
public sealed class ListeningSeats
{
    private readonly ConcurrentDictionary<ulong, ulong> _listening = new();

    // Set by the listening side: stops listening in that server.
    public Func<ulong, Task>? Release { get; set; }

    public bool IsListening(ulong guildId, ulong helperId) => _listening.TryGetValue(guildId, out var helper) && helper == helperId;

    public void Sit(ulong guildId, ulong helperId) => _listening[guildId] = helperId;

    public void Leave(ulong guildId) => _listening.TryRemove(guildId, out _);

    public async Task FreeAsync(ulong guildId, ulong helperId)
    {
        if (IsListening(guildId, helperId) && Release is { } release)
            await release(guildId);
    }
}
