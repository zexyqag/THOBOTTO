using NodaTime;

using THOBOTTO.Members;
using THOBOTTO.Modules;

namespace THOBOTTO.Events;

// The server's time zone is an events setting; members without their own read times in it.
public sealed class ServerTimeZone(SettingsStore settings) : IServerTimeZone
{
    public async Task<DateTimeZone?> ForAsync(ulong guildId)
        => TimeZones.Find((await settings.GetAsync<EventRules>(guildId, EventBoard.ModuleId)).TimeZone);
}
