using THOBOTTO.Music;
using THOBOTTO.Voice;

namespace THOBOTTO.Lastfm;

// A blend for a voice channel: the Last.fm taste of everyone there who linked an account, mixed,
// topped up with similar songs, and found on YouTube Music.
public sealed class BlendMaker(LastfmClient client, Scrobbler scrobbler, VoicePresence presence, MusicService music, ILogger<BlendMaker> logger)
{
    public const int DefaultSize = 25;
    public const int MaxSize = 50;
    private const int Lookups = 5;

    public bool Configured => client.Configured;

    public async Task<(IReadOnlyList<Track> Tracks, string Name, string? Problem)> BuildAsync(ulong guildId, ulong voiceChannelId, ulong requestedBy, int size)
    {
        if (!client.Configured)
            return ([], "", "Last.fm isn't set up on this bot.");

        // Everyone hearing that channel's music, the channels playing along included.
        var channels = music.PlayerIn(guildId, voiceChannelId)?.Channels.ToHashSet() ?? [voiceChannelId];
        var links = new List<LastfmLink>();
        foreach (var (userId, _) in presence.Snapshot(guildId).Where(p => !p.Value.IsBot && !p.Value.Deafened && channels.Contains(p.Value.ChannelId)))
        {
            if (await scrobbler.FindAsync(userId) is { } link)
                links.Add(link);
        }
        if (links.Count == 0)
            return ([], "", "Nobody in the channel has linked Last.fm yet; that's on the web panel's \"You\" page.");

        var tastes = (await Task.WhenAll(links.Select(TasteAsync))).OfType<Taste>().ToList();
        var songs = Blender.Mix(tastes, size).ToList();
        if (songs.Count < size)
            await TopUpAsync(songs, size);
        if (songs.Count == 0)
            return ([], "", "There's not enough listening history on Last.fm for a blend yet.");

        var tracks = await FindAsync(songs, requestedBy);
        var name = $"Blend: {string.Join(", ", links.Select(l => l.Username))}";
        return tracks.Count == 0 ? ([], name, "None of the songs could be found to play.") : (tracks, name, null);
    }

    private async Task<Taste?> TasteAsync(LastfmLink link)
    {
        try
        {
            return new(link.Username, await client.TopTracksAsync(link.Username), await client.LovedTracksAsync(link.Username));
        }
        catch (Exception ex) when (ex is LastfmException or HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            logger.LogWarning("Last.fm taste of {User}: {Message}", link.Username, ex.Message);
            return null;
        }
    }

    // Songs like the first few, for small or brand-new histories.
    private async Task TopUpAsync(List<Song> songs, int size)
    {
        var keys = songs.Select(s => s.Key).ToHashSet();
        foreach (var seed in songs.Take(5).ToList())
        {
            try
            {
                foreach (var similar in await client.SimilarAsync(seed))
                {
                    if (songs.Count >= size)
                        return;
                    if (keys.Add(similar.Key))
                        songs.Add(similar);
                }
            }
            catch (Exception ex) when (ex is LastfmException or HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
            {
                logger.LogDebug("Songs like {Title}: {Message}", seed.Title, ex.Message);
            }
        }
    }

    // Each song's best YouTube Music match, in the blend's order; ones not found drop out.
    private async Task<IReadOnlyList<Track>> FindAsync(IReadOnlyList<Song> songs, ulong requestedBy)
    {
        using var gate = new SemaphoreSlim(Lookups);
        var found = await Task.WhenAll(songs.Select(async song =>
        {
            await gate.WaitAsync();
            try
            {
                return (await LavalinkConnection.LoadAsync(music.Lavalink, $"ytmsearch:{song.Artist} {song.Title}", requestedBy)).Tracks.FirstOrDefault();
            }
            catch (HttpRequestException)
            {
                return null;
            }
            finally
            {
                gate.Release();
            }
        }));
        return found.OfType<Track>().ToList();
    }
}
