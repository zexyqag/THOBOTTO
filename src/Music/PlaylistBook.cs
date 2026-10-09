using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using THOBOTTO.Access;
using THOBOTTO.Data;

namespace THOBOTTO.Music;

// Saved playlists per server: anyone saves; the one who saved it (or music.manage) replaces or deletes it.
public sealed class PlaylistBook(IDbContextFactory<BotDbContext> dbFactory, TimeProvider time)
{
    public const int MaxNameLength = 50;
    public const int MaxPerServer = 200;

    public async Task<IReadOnlyList<SavedPlaylist>> ListAsync(ulong guildId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        // Without the tracks: a list only needs names and counts.
        return await db.SavedPlaylists.AsNoTracking().Where(p => p.GuildId == guildId).OrderBy(p => p.Key)
            .Select(p => new SavedPlaylist { Id = p.Id, GuildId = p.GuildId, Name = p.Name, Key = p.Key, CreatorId = p.CreatorId, Tracks = "", TrackCount = p.TrackCount, UpdatedAt = p.UpdatedAt })
            .ToListAsync();
    }

    public async Task<(SavedPlaylist Playlist, IReadOnlyList<Track> Tracks)?> FindAsync(ulong guildId, string name)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var key = name.Trim().ToLowerInvariant();
        return await db.SavedPlaylists.AsNoTracking().FirstOrDefaultAsync(p => p.GuildId == guildId && p.Key == key) is { } found
            ? (found, JsonSerializer.Deserialize<List<Track>>(found.Tracks)!)
            : null;
    }

    // Returns what happened, for the reply.
    public async Task<string> SaveAsync(ulong guildId, string name, ulong userId, bool mayReplaceAny, IReadOnlyList<Track> tracks)
    {
        name = name.Trim();
        if (name.Length is 0 or > MaxNameLength)
            return $"A playlist name is 1 to {MaxNameLength} characters.";
        if (tracks.Count == 0)
            return "There's nothing to save.";

        await using var db = await dbFactory.CreateDbContextAsync();
        var key = name.ToLowerInvariant();
        var existing = await db.SavedPlaylists.FirstOrDefaultAsync(p => p.GuildId == guildId && p.Key == key);
        if (existing is null)
        {
            if (await db.SavedPlaylists.CountAsync(p => p.GuildId == guildId) >= MaxPerServer)
                return $"This server has {MaxPerServer} playlists already; delete one first.";
            existing = new() { GuildId = guildId, Name = name, Key = key, Tracks = "" };
            db.SavedPlaylists.Add(existing);
        }
        else if (existing.CreatorId != userId && !mayReplaceAny)
            return $"**{existing.Name}** is someone else's playlist; pick another name.";

        existing.Name = name;
        existing.CreatorId = existing.Id == 0 ? userId : existing.CreatorId;
        existing.Tracks = JsonSerializer.Serialize(tracks);
        existing.TrackCount = tracks.Count;
        existing.UpdatedAt = time.GetUtcNow();
        var replaced = existing.Id != 0;
        await db.SaveChangesAsync();
        return $"💾 {(replaced ? "Replaced" : "Saved")} **{name}** ({tracks.Count} tracks).";
    }

    public async Task<string> DeleteAsync(ulong guildId, string name, ulong userId, bool mayDeleteAny)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var key = name.Trim().ToLowerInvariant();
        if (await db.SavedPlaylists.FirstOrDefaultAsync(p => p.GuildId == guildId && p.Key == key) is not { } found)
            return "No playlist by that name.";
        if (found.CreatorId != userId && !mayDeleteAny)
            return $"**{found.Name}** is someone else's playlist; deleting it needs `{BotPermissions.ManageMusic}`.";
        db.SavedPlaylists.Remove(found);
        await db.SaveChangesAsync();
        return $"🗑️ Deleted **{found.Name}**.";
    }
}
