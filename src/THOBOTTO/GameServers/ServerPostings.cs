using Microsoft.EntityFrameworkCore;

using NetCord.Gateway;

using THOBOTTO.Data;
using THOBOTTO.Modules;

namespace THOBOTTO.GameServers;

public sealed class ServerPostings(IDbContextFactory<BotDbContext> dbFactory) : IPostingSource
{
    public async Task<IReadOnlyList<Posting>> PostingsAsync(Guild guild)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var bridged = await db.GameServers.AsNoTracking().Where(s => s.GuildId == guild.Id && s.BridgeChannelId != null).ToListAsync();
        return [.. bridged.Select(s => new Posting(ServerWatch.ModuleId, $"The chat of {s.Name ?? s.Game}", new Landing.In(s.BridgeChannelId!.Value), $"/g/{guild.Id}/servers"))];
    }
}
