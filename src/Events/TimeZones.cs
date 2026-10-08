using Microsoft.EntityFrameworkCore;

using NodaTime;

using THOBOTTO.Data;
using THOBOTTO.Modules;

namespace THOBOTTO.Events;

public sealed class TimeZones(IDbContextFactory<BotDbContext> dbFactory, SettingsStore settings)
{
    public static IReadOnlyList<string> Ids => DateTimeZoneProviders.Tzdb.Ids;

    public static DateTimeZone? Find(string id) => DateTimeZoneProviders.Tzdb.GetZoneOrNull(id);

    // The member's own zone, else the server's. Also says whose it is, for the "wrong?" hint.
    public async Task<(DateTimeZone Zone, bool Own)> ForAsync(ulong guildId, ulong userId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        if (await db.MemberTimeZones.FindAsync(userId) is { } mine && Find(mine.Zone) is { } own)
            return (own, true);

        var rules = await settings.GetAsync<EventRules>(guildId, EventBoard.ModuleId);
        return (Find(rules.TimeZone) ?? DateTimeZone.Utc, false);
    }

    public async Task SetAsync(ulong userId, string zone)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var row = await db.MemberTimeZones.FindAsync(userId);
        if (row is null)
            db.MemberTimeZones.Add(new() { UserId = userId, Zone = zone });
        else
            row.Zone = zone;
        await db.SaveChangesAsync();
    }

    // Back to the server's zone.
    public async Task ClearAsync(ulong userId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.MemberTimeZones.Where(z => z.UserId == userId).ExecuteDeleteAsync();
    }
}
