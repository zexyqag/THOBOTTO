using Microsoft.EntityFrameworkCore;

using THOBOTTO.Data;
using THOBOTTO.Lastfm;

namespace THOBOTTO.Stats;

// An artist's genres from Last.fm, kept once looked up.
public sealed class ArtistGenres
{
    // The artist's name lowercased.
    public required string Artist { get; init; }

    // Up to three, most fitting first; empty when Last.fm knows none.
    public List<string> Genres { get; set; } = [];

    public DateTimeOffset FetchedAt { get; set; }
}

// Genres for Wrapped, from Last.fm's artist tags (none without a Last.fm key).
public sealed class GenreBook(LastfmClient client, IDbContextFactory<BotDbContext> dbFactory, TimeProvider time, ILogger<GenreBook> logger)
{
    private const int MaxGenres = 3;
    private const int MinWeight = 20;
    private const int Lookups = 4;
    private static readonly TimeSpan Fresh = TimeSpan.FromDays(90);

    // Tags people use that aren't genres.
    private static readonly HashSet<string> NotGenres = new(StringComparer.OrdinalIgnoreCase)
    {
        "seen live", "favorites", "favourites", "favorite", "favourite", "my favorite", "love", "awesome", "beautiful", "cool", "amazing",
        "male vocalists", "female vocalists", "male vocalist", "female vocalist", "under 2000 listeners", "spotify", "albums i own",
        "american", "british", "uk", "usa", "english", "swedish", "australian", "german", "canadian", "japanese", "korean", "french", "norwegian", "danish",
    };

    public bool Available => client.Configured;

    // Genres per artist (lowercased name), looking up the ones not known yet or gone stale.
    public async Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> ForAsync(IEnumerable<string> artists)
    {
        var wanted = artists.Select(a => a.ToLowerInvariant()).Distinct().ToList();
        await using var db = await dbFactory.CreateDbContextAsync();
        var known = await db.ArtistGenres.Where(g => wanted.Contains(g.Artist)).ToDictionaryAsync(g => g.Artist);
        if (client.Configured)
        {
            var stale = wanted.Where(a => !known.TryGetValue(a, out var k) || time.GetUtcNow() - k.FetchedAt > Fresh).ToList();
            using var gate = new SemaphoreSlim(Lookups);
            var fetched = await Task.WhenAll(stale.Select(async artist =>
            {
                await gate.WaitAsync();
                try
                {
                    return (Artist: artist, Genres: Pick(artist, await client.ArtistTagsAsync(artist)));
                }
                catch (Exception ex) when (ex is LastfmException or HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
                {
                    logger.LogDebug("Genres of {Artist}: {Message}", artist, ex.Message);
                    return (Artist: artist, Genres: (List<string>?)null);
                }
                finally
                {
                    gate.Release();
                }
            }));
            foreach (var (artist, genres) in fetched.Where(f => f.Genres is not null))
            {
                if (!known.TryGetValue(artist, out var row))
                    db.ArtistGenres.Add(row = known[artist] = new() { Artist = artist });
                (row.Genres, row.FetchedAt) = (genres!, time.GetUtcNow());
            }
            await db.SaveChangesAsync();
        }
        return known.ToDictionary(k => k.Key, k => (IReadOnlyList<string>)k.Value.Genres);
    }

    public static List<string> Pick(string artist, IReadOnlyList<(string Tag, int Weight)> tags)
        => tags.Where(t => t.Weight >= MinWeight && !NotGenres.Contains(t.Tag) && !t.Tag.Equals(artist, StringComparison.OrdinalIgnoreCase) && t.Tag.Length is > 1 and <= 30)
            .Select(t => t.Tag.ToLowerInvariant())
            .Distinct()
            .Take(MaxGenres)
            .ToList();
}
