using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using THOBOTTO.Data;
using THOBOTTO.Members;

namespace THOBOTTO.Moderation;

// How many moderation cases are about the member: the number alone, the cases being the moderators' record.
public sealed class CasesMemberData(IDbContextFactory<BotDbContext> dbFactory) : IMemberData
{
    public string Name => "Cases";

    public async Task<long> CountAsync(ulong guildId, ulong userId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.ModCases.LongCountAsync(c => c.GuildId == guildId && c.TargetId == userId);
    }

    public async Task WriteAsync(ulong guildId, ulong userId, Utf8JsonWriter json) => json.WriteNumberValue(await CountAsync(guildId, userId));
}
