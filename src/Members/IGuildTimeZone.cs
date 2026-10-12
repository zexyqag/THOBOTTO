using NodaTime;

namespace THOBOTTO.Members;

// The zone a server reads times in, for members who haven't set their own; whoever keeps that setting provides it.
public interface IGuildTimeZone
{
    Task<DateTimeZone?> ForAsync(ulong guildId);
}
