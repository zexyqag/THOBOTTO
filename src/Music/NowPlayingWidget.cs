using THOBOTTO.Overview;

namespace THOBOTTO.Music;

// What the helpers play right now, channel by channel.
public sealed class NowPlayingWidget(MusicService music) : IWidgetKind
{
    public string Id => "now-playing";

    public string Name => "Now playing";

    public string Description => "what plays in each voice channel right now";

    public TimeSpan Refresh => TimeSpan.FromSeconds(30);

    public Task<IReadOnlyList<WidgetBlock>> RenderAsync(ulong guildId, IReadOnlyDictionary<string, string> options)
    {
        var playing = music.Players.Where(p => p.GuildId == guildId && p.Current is not null).OrderBy(p => p.VoiceChannelId).ToList();
        if (playing.Count == 0)
            return Task.FromResult<IReadOnlyList<WidgetBlock>>([new("Now playing", "🎵 Nothing is playing right now.")]);
        var lines = playing.Select(p => $"<#{p.VoiceChannelId}>: {p.Current!.Markdown} · {p.Current.Author}{(p.Paused ? " (paused)" : "")} · asked for by <@{p.Current.RequestedBy}>");
        return Task.FromResult<IReadOnlyList<WidgetBlock>>([new("Now playing", string.Join('\n', lines))]);
    }
}
