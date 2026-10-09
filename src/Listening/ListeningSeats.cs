using System.Collections.Concurrent;

namespace THOBOTTO.Listening;

// Which helper sits listening in which server, so music doesn't send it to play there meanwhile.
public sealed class ListeningSeats
{
    private readonly ConcurrentDictionary<ulong, ulong> _listening = new();

    public bool IsListening(ulong guildId, ulong helperId) => _listening.TryGetValue(guildId, out var helper) && helper == helperId;

    public void Sit(ulong guildId, ulong helperId) => _listening[guildId] = helperId;

    public void Leave(ulong guildId) => _listening.TryRemove(guildId, out _);
}
