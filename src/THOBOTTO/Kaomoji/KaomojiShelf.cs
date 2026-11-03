using System.Collections.Concurrent;

using Microsoft.EntityFrameworkCore;

using NetCord.Gateway;

using THOBOTTO.Data;
using THOBOTTO.Modules;
using THOBOTTO.Panel;

namespace THOBOTTO.Kaomoji;

// A face a server added for itself, beside the library's.
public sealed class ServerKaomoji
{
    public long Id { get; init; }

    public ulong GuildId { get; init; }

    public required string Text { get; init; }

    public required string Name { get; init; }

    // Other words that find it, comma separated.
    public string Tags { get; init; } = "";

    public ulong AddedById { get; init; }

    public DateTimeOffset AddedAt { get; init; }
}

public sealed class KaomojiShelf(IDbContextFactory<BotDbContext> dbFactory, ModuleState modules, PanelAccess members, GatewayClient gateway, TimeProvider time)
{
    public const string ModuleId = "kaomoji";
    public const int TextLength = 80;
    public const int NameLength = 60;
    public const int TagsLength = 200;
    public const int Most = 500;

    private readonly ConcurrentDictionary<ulong, IReadOnlyList<Face>> _own = new();

    // What /kaomoji offers: in a server with the bot, its own then the library (null while it has kaomoji off);
    // anywhere else, the own of the servers the person shares with the bot that have kaomoji on, then the library.
    public async Task<IReadOnlyList<Face>?> ForAsync(ulong? guildId, ulong userId)
    {
        if (guildId is { } id && gateway.Cache.Guilds.ContainsKey(id))
            return await modules.IsEnabledAsync(id, ModuleId) ? await InAsync(id) : null;
        var own = new List<Face>();
        foreach (var guild in await members.GuildsAsync(userId))
        {
            if (await modules.IsEnabledAsync(guild.Id, ModuleId))
                own.AddRange(await OwnAsync(guild.Id));
        }
        return [.. own, .. KaomojiLibrary.All];
    }

    public async Task<IReadOnlyList<Face>> InAsync(ulong guildId) => [.. await OwnAsync(guildId), .. KaomojiLibrary.All];

    private async Task<IReadOnlyList<Face>> OwnAsync(ulong guildId)
    {
        if (_own.TryGetValue(guildId, out var cached))
            return cached;
        var faces = (await OfAsync(guildId)).Select(k => new Face($"s{k.Id}", k.Text, k.Name, k.Tags.Split(',', StringSplitOptions.RemoveEmptyEntries))).ToList();
        return _own[guildId] = faces;
    }

    public async Task<IReadOnlyList<ServerKaomoji>> OfAsync(ulong guildId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.ServerKaomojis.AsNoTracking().Where(k => k.GuildId == guildId).OrderBy(k => k.Name).ToListAsync();
    }

    public async Task<ServerKaomoji?> FindAsync(ulong guildId, long id)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.ServerKaomojis.FirstOrDefaultAsync(k => k.GuildId == guildId && k.Id == id);
    }

    // Null once added, else why not.
    public async Task<string?> AddAsync(ulong guildId, ulong userId, string text, string name, string tags)
    {
        (text, name) = (text.Trim(), name.Trim());
        var words = string.Join(',', tags.Split(',').Select(t => t.Trim().ToLowerInvariant()).Where(t => t.Length > 0).Distinct());
        if (text.Length is 0 or > TextLength || text.Contains('\n'))
            return $"The face is 1 to {TextLength} characters, on one line.";
        if (name.Length is 0 or > NameLength)
            return $"Its name is 1 to {NameLength} characters.";
        if (words.Length > TagsLength)
            return $"Other words are at most {TagsLength} characters.";
        await using var db = await dbFactory.CreateDbContextAsync();
        if (await db.ServerKaomojis.AnyAsync(k => k.GuildId == guildId && k.Text == text))
            return "This server has that one already.";
        if (await db.ServerKaomojis.CountAsync(k => k.GuildId == guildId) >= Most)
            return $"This server has {Most} of its own: remove one first.";
        db.ServerKaomojis.Add(new() { GuildId = guildId, Text = text, Name = name, Tags = words, AddedById = userId, AddedAt = time.GetUtcNow() });
        db.AuditEntries.Add(new() { GuildId = guildId, ActorId = userId, Action = "kaomoji.add", Details = $"{text} ({name})", CreatedAt = time.GetUtcNow() });
        await db.SaveChangesAsync();
        _own.TryRemove(guildId, out _);
        return null;
    }

    public async Task RemoveAsync(ServerKaomoji kaomoji, ulong actorId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.ServerKaomojis.Where(k => k.Id == kaomoji.Id).ExecuteDeleteAsync();
        db.AuditEntries.Add(new()
        {
            GuildId = kaomoji.GuildId,
            ActorId = actorId,
            Action = "kaomoji.remove",
            Details = $"{kaomoji.Text} ({kaomoji.Name}) added by {kaomoji.AddedById}",
            CreatedAt = time.GetUtcNow(),
        });
        await db.SaveChangesAsync();
        _own.TryRemove(kaomoji.GuildId, out _);
    }
}
