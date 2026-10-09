namespace THOBOTTO.Lastfm;

// One listener's taste: their top tracks (best first) and loved tracks.
public sealed record Taste(string Name, IReadOnlyList<Song> Top, IReadOnlyList<Song> Loved);

// Mixes several people's taste into one list: songs they share first, by how much they all like them,
// then each person's favourites in turns (artists others like too come sooner), the two interleaved.
public static class Blender
{
    private const double LovedBonus = 0.6;
    private const double SharedArtistBonus = 0.5;

    public static IReadOnlyList<Song> Mix(IReadOnlyList<Taste> tastes, int size)
    {
        var liking = tastes.Select(Liking).ToList();
        var songs = liking.SelectMany(l => l.Values).GroupBy(s => s.Song.Key).ToDictionary(g => g.Key, g => g.First().Song);

        // A song in two or more people's lists, ranked by everyone's liking together.
        var shared = songs.Keys
            .Select(key => (Key: key, Fans: liking.Count(l => l.ContainsKey(key)), Score: liking.Sum(l => l.GetValueOrDefault(key).Score)))
            .Where(s => s.Fans >= 2)
            .OrderByDescending(s => s.Fans).ThenByDescending(s => s.Score)
            .Select(s => s.Key)
            .Take(size / 2)
            .ToList();

        // Each person's own picks: artists the others like as well move up.
        var artistFans = liking.SelectMany(l => l.Values.Select(v => v.Song.Artist.ToLowerInvariant()).Distinct()).GroupBy(a => a).ToDictionary(g => g.Key, g => g.Count());
        var turns = liking.Select(l => new Queue<string>(l.Values
            .OrderByDescending(v => v.Score + SharedArtistBonus * (artistFans[v.Song.Artist.ToLowerInvariant()] - 1))
            .Select(v => v.Song.Key))).ToList();

        var taken = shared.ToHashSet();
        var own = new List<string>();
        while (own.Count + shared.Count < size && turns.Any(t => t.Count > 0))
        {
            foreach (var turn in turns)
            {
                while (turn.TryDequeue(out var key))
                {
                    if (taken.Add(key))
                    {
                        own.Add(key);
                        break;
                    }
                }
                if (own.Count + shared.Count >= size)
                    break;
            }
        }

        // Shared and own picks take turns, so the start isn't all one kind.
        var mixed = new List<Song>();
        for (var i = 0; i < Math.Max(shared.Count, own.Count); i++)
        {
            if (i < shared.Count)
                mixed.Add(songs[shared[i]]);
            if (i < own.Count)
                mixed.Add(songs[own[i]]);
        }
        return mixed.Take(size).ToList();
    }

    // How much one person likes each song: top tracks from 1 down by rank, loved ones a bit more.
    private static Dictionary<string, (Song Song, double Score)> Liking(Taste taste)
    {
        var liking = new Dictionary<string, (Song Song, double Score)>();
        for (var rank = 0; rank < taste.Top.Count; rank++)
            liking.TryAdd(taste.Top[rank].Key, (taste.Top[rank], Math.Max(0.1, 1 - (double)rank / taste.Top.Count)));
        foreach (var song in taste.Loved)
            liking[song.Key] = (liking.GetValueOrDefault(song.Key).Song ?? song, liking.GetValueOrDefault(song.Key).Score + LovedBonus);
        return liking;
    }
}
