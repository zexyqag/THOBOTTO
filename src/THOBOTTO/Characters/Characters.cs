using System.Collections.Concurrent;
using System.Security.Cryptography;

using Microsoft.EntityFrameworkCore;

using THOBOTTO.Data;
using THOBOTTO.Modules;
using THOBOTTO.Panel;
using THOBOTTO.Reposts;

namespace THOBOTTO.Characters;

// Someone's character: a name and a picture their messages can go under, picked by the brackets they're written in
// ("g:hello" or "[hello]"). Theirs in every server that has characters on.
public sealed class Character
{
    public long Id { get; init; }

    public ulong UserId { get; init; }

    public required string Name { get; set; }

    public string Prefix { get; set; } = "";

    public string Suffix { get; set; } = "";

    public byte[]? Avatar { get; set; }

    public string? AvatarType { get; set; }

    // In the picture's address, new with each picture, so an old address stops working and Discord fetches it anew.
    public string AvatarKey { get; set; } = "";

    public DateTimeOffset CreatedAt { get; init; }

    public string Brackets => $"{Prefix}text{Suffix}";
}

// Stored with SettingsStore under the module id.
public sealed record CharacterRules
{
    [Setting("Only in these channels", Help = "Empty: anywhere. Elsewhere a message in a character's brackets stays as written.", Kind = SettingKind.TextChannel)]
    public IReadOnlyList<ulong> ChannelIds { get; init; } = [];
}

public sealed class CharacterBook(IDbContextFactory<BotDbContext> dbFactory, PanelLinks links, TimeProvider time) : IHostedService
{
    public const string ModuleId = "characters";
    public const int Most = 25;
    public const int NameLength = 80;
    public const int BracketLength = 20;
    public const int AvatarBytes = 1024 * 1024;

    private static readonly string[] Pictures = ["image/png", "image/jpeg", "image/gif", "image/webp"];

    // Everyone's characters without their pictures, by owner: messages are looked at against these.
    private readonly ConcurrentDictionary<ulong, IReadOnlyList<Character>> _known = new();
    private readonly SemaphoreSlim _loading = new(1, 1);
    private bool _loaded;

    public bool Has(ulong userId) => _known.ContainsKey(userId);

    public async Task<IReadOnlyList<Character>> OfAsync(ulong userId)
    {
        await LoadAsync();
        return _known.GetValueOrDefault(userId) ?? [];
    }

    // The character whose brackets the text is written in (the most specific), and the text inside them.
    public static (Character Character, string Text)? In(IReadOnlyList<Character> characters, string text)
    {
        foreach (var character in characters.OrderByDescending(c => c.Prefix.Length + c.Suffix.Length))
        {
            if (text.Length >= character.Prefix.Length + character.Suffix.Length
                && text.StartsWith(character.Prefix, StringComparison.OrdinalIgnoreCase) && text.EndsWith(character.Suffix, StringComparison.OrdinalIgnoreCase))
                return (character, text[character.Prefix.Length..^character.Suffix.Length].Trim());
        }
        return null;
    }

    // "g:text" or "[text]" as prefix and suffix; null if it isn't brackets around the word text.
    public static (string Prefix, string Suffix)? Parse(string brackets)
    {
        var at = brackets.IndexOf("text", StringComparison.OrdinalIgnoreCase);
        if (at < 0 || brackets.IndexOf("text", at + 4, StringComparison.OrdinalIgnoreCase) >= 0)
            return null;
        var (prefix, suffix) = (brackets[..at].TrimStart(), brackets[(at + 4)..].TrimEnd());
        return prefix.Length + suffix.Length == 0 || prefix.Length > BracketLength || suffix.Length > BracketLength ? null : (prefix, suffix);
    }

    // The picture's address Discord can fetch: on the panel, when it has a public https address.
    public async Task<string?> AvatarUrlAsync(Character character)
        => character.AvatarKey.Length > 0 && await links.AddressAsync() is { } address && address.StartsWith("https://", StringComparison.Ordinal)
            ? $"{address}/characters/{character.Id}/{character.AvatarKey}"
            : null;

    public async Task<Character?> AvatarAsync(long id, string key)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Characters.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id && c.AvatarKey == key && c.Avatar != null);
    }

    // Null once saved, else why not. Name, brackets and picture each left as they were when not given.
    public async Task<string?> SaveAsync(ulong userId, long? id, string? name, string? brackets, (byte[] Bytes, string Type)? avatar)
    {
        name = name?.Trim();
        if (name is { Length: 0 or > NameLength })
            return $"A name is 1 to {NameLength} characters.";
        var parsed = brackets is null ? null : Parse(brackets);
        if (brackets is not null && parsed is null)
            return $"Brackets are what goes around the word text, like g:text or [text], at most {BracketLength} characters each side.";
        if (avatar is { } picture && (picture.Bytes.Length > AvatarBytes || !Pictures.Contains(picture.Type)))
            return $"A picture is a PNG, JPEG, GIF or WebP of at most {AvatarBytes / 1024 / 1024} MB.";
        await using var db = await dbFactory.CreateDbContextAsync();
        var mine = await db.Characters.Where(c => c.UserId == userId).ToListAsync();
        var character = id is { } existing ? mine.FirstOrDefault(c => c.Id == existing) : null;
        if (id is not null && character is null)
            return "That character isn't yours.";
        if (character is null)
        {
            if (mine.Count >= Most)
                return $"You have {Most} characters: remove one first.";
            if (name is null || parsed is null)
                return "A new character needs a name and brackets.";
            character = new() { UserId = userId, Name = name, CreatedAt = time.GetUtcNow() };
            db.Characters.Add(character);
        }
        if (parsed is (var prefix, var suffix))
        {
            if (mine.Any(c => c != character && c.Prefix.Equals(prefix, StringComparison.OrdinalIgnoreCase) && c.Suffix.Equals(suffix, StringComparison.OrdinalIgnoreCase)))
                return "Another of your characters has those brackets.";
            (character.Prefix, character.Suffix) = (prefix, suffix);
        }
        if (name is not null)
            character.Name = name;
        if (avatar is { } given)
            (character.Avatar, character.AvatarType, character.AvatarKey) = (given.Bytes, given.Type, Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(12)));
        await db.SaveChangesAsync();
        await ReloadAsync(userId);
        return null;
    }

    public async Task<bool> RemoveAsync(ulong userId, long id)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var removed = await db.Characters.Where(c => c.UserId == userId && c.Id == id).ExecuteDeleteAsync() > 0;
        await ReloadAsync(userId);
        return removed;
    }

    public Task StartAsync(CancellationToken cancellationToken) => LoadAsync();

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task LoadAsync()
    {
        if (_loaded)
            return;
        await _loading.WaitAsync();
        try
        {
            if (_loaded)
                return;
            await using var db = await dbFactory.CreateDbContextAsync();
            foreach (var group in (await Plain(db.Characters).ToListAsync()).GroupBy(c => c.UserId))
                _known[group.Key] = [.. group.OrderBy(c => c.Name)];
            _loaded = true;
        }
        finally
        {
            _loading.Release();
        }
    }

    private async Task ReloadAsync(ulong userId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var mine = await Plain(db.Characters.Where(c => c.UserId == userId)).OrderBy(c => c.Name).ToListAsync();
        if (mine.Count == 0)
            _known.TryRemove(userId, out _);
        else
            _known[userId] = mine;
    }

    private static IQueryable<Character> Plain(IQueryable<Character> characters)
        => characters.AsNoTracking().Select(c => new Character
        {
            Id = c.Id,
            UserId = c.UserId,
            Name = c.Name,
            Prefix = c.Prefix,
            Suffix = c.Suffix,
            AvatarType = c.AvatarType,
            AvatarKey = c.AvatarKey,
            CreatedAt = c.CreatedAt,
        });
}

// A message in one of the member's characters' brackets, posted again under that character's name and picture.
public sealed class CharacterVoice(CharacterBook book, SettingsStore settings, ModuleState modules) : IRewrite
{
    public RewriteStage Stage => RewriteStage.Who;

    public bool Wants(string text, ulong channelId, ulong userId) => book.Has(userId);

    public async Task RewriteAsync(Repost repost, PostingPreference preference)
    {
        var guildId = repost.Guild.Id;
        if (!await modules.IsEnabledAsync(guildId, CharacterBook.ModuleId)
            || CharacterBook.In(await book.OfAsync(repost.Member.Id), repost.Text) is not { } found
            || (found.Text.Length == 0 && repost.Message?.Attachments.Count is null or 0))
            return;
        var channels = (await settings.GetAsync<CharacterRules>(guildId, CharacterBook.ModuleId)).ChannelIds;
        if (channels.Count > 0 && !channels.Contains(repost.Channel is NetCord.GuildThread thread ? thread.ParentId : repost.Channel.Id))
            return;
        (repost.Text, repost.Name, repost.AvatarUrl, repost.Changed) = (found.Text, found.Character.Name, await book.AvatarUrlAsync(found.Character), true);
    }
}
