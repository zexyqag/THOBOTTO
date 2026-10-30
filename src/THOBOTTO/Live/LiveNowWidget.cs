using THOBOTTO.Overview;

namespace THOBOTTO.Live;

// Who's live right now.
public sealed class LiveNowWidget(LiveWatch watch) : IWidgetKind
{
    public string Id => "live";

    public string Name => "Live now";

    public string Description => "members streaming right now, on Twitch or YouTube";

    public TimeSpan Refresh => TimeSpan.FromMinutes(1);

    public async Task<IReadOnlyList<WidgetBlock>> RenderAsync(ulong guildId, IReadOnlyDictionary<string, string> options)
    {
        var live = await watch.LiveAsync(guildId);
        var text = live.Count == 0
            ? "Nobody's live right now."
            : string.Join('\n', live.Select(s => $"🔴 <@{s.UserId}> on [{s.Platform}]({s.Url}){(s.Title is { } title ? $": {title}" : "")}{(s.Game is { } game ? $" · {game}" : "")} · since <t:{s.StartedAt.ToUnixTimeSeconds()}:R>"));
        return [new("Live now", text)];
    }
}
