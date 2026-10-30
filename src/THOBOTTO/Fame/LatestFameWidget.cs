using Microsoft.EntityFrameworkCore;

using THOBOTTO.Data;
using THOBOTTO.Notifications;
using THOBOTTO.Overview;

namespace THOBOTTO.Fame;

// The latest messages in the hall of fame, with the start of each when the archive has it.
public sealed class LatestFameWidget(IDbContextFactory<BotDbContext> dbFactory) : IWidgetKind
{
    private const int Snippet = 120;

    public string Id => "hall-of-fame";

    public string Name => "Hall of fame";

    public string Description => "the latest messages in the hall of fame";

    public IReadOnlyList<WidgetOption> Options => [new("count", "How many", "5", "1 to 10")];

    public TimeSpan Refresh => TimeSpan.FromMinutes(5);

    public async Task<IReadOnlyList<WidgetBlock>> RenderAsync(ulong guildId, IReadOnlyDictionary<string, string> options)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var entries = await db.FameEntries.AsNoTracking().Where(e => e.GuildId == guildId).OrderByDescending(e => e.InductedAt)
            .Take(WidgetOptions.Int(options, "count", 5, 1, 10)).ToListAsync();
        if (entries.Count == 0)
            return [];
        var ids = entries.Select(e => e.MessageId).ToList();
        var contents = await db.ArchivedMessages.AsNoTracking().Where(m => ids.Contains(m.Id) && m.Content != null && m.Content != "")
            .ToDictionaryAsync(m => m.Id, m => m.Content!);
        var lines = entries.Select(e =>
        {
            var line = $"⭐ <@{e.AuthorId}> in <#{e.ChannelId}> · {e.Reactors} reacted · <t:{e.InductedAt.ToUnixTimeSeconds()}:R> · [the message]({Notifier.Link(e.GuildId, e.ChannelId, e.MessageId)})";
            return contents.TryGetValue(e.MessageId, out var content) ? $"{line}\n> {FirstLine(content)}" : line;
        });
        return [new("Hall of fame", string.Join('\n', lines))];
    }

    private static string FirstLine(string content)
    {
        var line = content.ReplaceLineEndings("\n").Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        return line.Length <= Snippet ? line : line[..(Snippet - 1)] + "…";
    }
}
