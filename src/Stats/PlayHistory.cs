using Microsoft.EntityFrameworkCore;

using THOBOTTO.Data;
using THOBOTTO.Helpers;
using THOBOTTO.Lastfm;
using THOBOTTO.Music;

namespace THOBOTTO.Stats;

// Writes down every song the helpers finish or move on from.
public sealed class PlayHistory(IDbContextFactory<BotDbContext> dbFactory, PersonalityBook personalities, ILogger<PlayHistory> logger)
{
    // Ending this close to the end counts as played through.
    private static readonly TimeSpan Slack = TimeSpan.FromSeconds(5);

    public void Record(EndedListen? ended)
    {
        if (ended is { Failed: false })
            _ = Task.Run(() => WriteAsync(ended));
    }

    private async Task WriteAsync(EndedListen ended)
    {
        var (player, track) = (ended.Listen.Player, ended.Listen.Track);
        try
        {
            var (artist, title) = TrackNames.Tidy(track);
            await using var db = await dbFactory.CreateDbContextAsync();
            var play = new PlayRecord
            {
                GuildId = player.GuildId,
                VoiceChannelId = player.VoiceChannelId,
                HelperId = player.Helper.UserId,
                Personality = (await personalities.WornAsync(player.GuildId, player.Helper.UserId))?.Name,
                Artist = artist,
                Title = title,
                Source = track.Source,
                Uri = track.Uri,
                LengthMs = track.IsStream ? 0 : track.LengthMs,
                PlayedMs = (long)ended.Played.TotalMilliseconds,
                Skipped = !track.IsStream && ended.Played < TimeSpan.FromMilliseconds(track.LengthMs) - Slack,
                Autoplay = track.RequestedBy == player.Helper.UserId,
                RequestedBy = track.RequestedBy,
                StartedAt = ended.Listen.StartedAt,
            };
            db.PlayRecords.Add(play);
            await db.SaveChangesAsync();
            db.PlayListeners.AddRange(ended.Listen.Listeners.Union(ended.ListenersAtEnd).Select(u => new PlayListener { PlayId = play.Id, UserId = u }));
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Recording a play of {Title} failed", track.Title);
        }
    }
}
