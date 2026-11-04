using System.Net;

using Microsoft.EntityFrameworkCore;

using NetCord;
using NetCord.Rest;

using THOBOTTO.Data;
using THOBOTTO.Discord;

namespace THOBOTTO.Expressions;

// An emoji the bot can use for members by name, with the name it has on Discord.
public sealed record UsableEmoji(string Name, ulong Id, bool Animated, string RealName)
{
    public static UsableEmoji Of(CustomEmoji emoji) => new(emoji.Name, emoji.Id, emoji.Animated, emoji.Name);

    public override string ToString() => $"<{(Animated ? "a" : "")}:{RealName}:{Id}>";
}

// A library sticker the bot can post for members, as its picture.
public sealed record LibrarySticker(long Id, string Name, string ContentType, ulong CreatorId);

// The library: every accepted emoji and sticker, any number of them; each emoji also the bot's own so it can use it
// anywhere, each sticker posted as its picture. Discord's limited lists hold the pinned ones and, as set, the most
// used, the way the sound board does.
public sealed partial class ExpressionShelf
{
    private const int AppEmojiAtOnce = 10;

    private readonly DailySwaps _emojiSwaps = new(time);
    private readonly DailySwaps _stickerSwaps = new(time);

    // This server's library emoji that aren't on Discord's list, to use through the bot.
    public async Task<IReadOnlyList<UsableEmoji>> LibraryAsync(ulong guildId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Expressions.AsNoTracking()
            .Where(e => e.GuildId == guildId && e.Kind == ExpressionKinds.Emoji && e.State == ExpressionStates.Live && e.DiscordId == null && e.AppEmojiId != null)
            .OrderBy(e => e.Name)
            .Select(e => new UsableEmoji(e.Name, e.AppEmojiId!.Value, e.Animated, e.AppEmojiName!))
            .ToListAsync();
    }

    // This server's library stickers, on Discord's list or not, to post as pictures.
    public async Task<IReadOnlyList<LibrarySticker>> StickersAsync(ulong guildId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Expressions.AsNoTracking()
            .Where(e => e.GuildId == guildId && e.Kind == ExpressionKinds.Sticker && e.State == ExpressionStates.Live)
            .OrderBy(e => e.Name)
            .Select(e => new LibrarySticker(e.Id, e.Name, e.ContentType, e.CreatorId))
            .ToListAsync();
    }

    // For the Emoji page: the ranking by uses the most-used policy works from, Discord's slots and today's swaps.
    public async Task<SlotView> LibraryViewAsync(ulong guildId, string kind)
    {
        var rules = await settings.GetAsync<ExpressionRules>(guildId, ModuleId);
        await using var db = await dbFactory.CreateDbContextAsync();
        var ranked = await RankedAsync(db, guildId, kind, rules, _ => true, CancellationToken.None);
        var used = ranked.Count(r => r.Item.OnDiscord);
        var (slots, swaps) = kind == ExpressionKinds.Emoji ? (2 * Capacity(guildId, kind), _emojiSwaps) : (Capacity(guildId, kind), _stickerSwaps);
        return new(SlotPolicy.Rank(ranked), rules.MostUsedDays, slots, used, swaps.Left(guildId, rules.SwapsPerDay), rules.SwapsPerDay);
    }

    // Pinned: on Discord's list now (making room if it must) and kept there. Unpinned: the policy may swap it out.
    public async Task<string> PinAsync(ulong guildId, long id, bool pinned)
    {
        await _gate.WaitAsync();
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            if (await LibraryItemAsync(db, guildId, id) is not { } item)
                return "That isn't in the library.";
            (item.Pinned, item.DiscordRemovedAt) = (pinned, pinned ? null : item.DiscordRemovedAt);
            await db.SaveChangesAsync();
            if (!pinned)
                return $"{Called(item)} is unpinned: it stays on Discord's list while it's used enough.";
            await KeepListAsync(guildId, await settings.GetAsync<ExpressionRules>(guildId, ModuleId), only: item.Id);
            await db.Entry(item).ReloadAsync();
            return item.DiscordId is not null ? $"{Called(item)} is pinned on Discord's list." : $"{Called(item)} is pinned, but Discord's list has no room it may take.";
        }
        finally
        {
            _gate.Release();
        }
    }

    // Off Discord's list until pinned again; still in the library.
    public async Task<string> TakeOffAsync(ulong guildId, long id)
    {
        await _gate.WaitAsync();
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            if (await LibraryItemAsync(db, guildId, id) is not { } item)
                return "That isn't in the library.";
            await PullAsync(db, item);
            (item.Pinned, item.DiscordRemovedAt) = (false, time.GetUtcNow());
            await db.SaveChangesAsync();
            return item.Kind == ExpressionKinds.Emoji
                ? $"{Called(item)} is off Discord's list; members still use it with :{item.Name}:."
                : $"{Called(item)} is off Discord's list; members still send it with /sticker send or sticker:{item.Name}.";
        }
        finally
        {
            _gate.Release();
        }
    }

    // Out of the library, as retiring would: the image kept, so it can be revived.
    public async Task<string> RetireAsync(ulong guildId, long id)
    {
        await _gate.WaitAsync();
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            if (await LibraryItemAsync(db, guildId, id) is not { } item)
                return "That isn't in the library.";
            await RetireAsync(db, item);
            return $"{Called(item)} is retired; it can be revived.";
        }
        finally
        {
            _gate.Release();
        }
    }

    // How replies name one: :name: for an emoji, the sticker “name”.
    private static string Called(Expression e) => e.Kind == ExpressionKinds.Emoji ? $":{e.Name}:" : $"The sticker “{e.Name}”";

    private static Task<Expression?> LibraryItemAsync(BotDbContext db, ulong guildId, long id)
        => db.Expressions.FirstOrDefaultAsync(e => e.GuildId == guildId && e.Id == id && e.State == ExpressionStates.Live);

    // Emoji: this many slots for static and as many for animated. Stickers: this many in all.
    private int Capacity(ulong guildId, string kind)
    {
        var tier = gateway.Cache.Guilds.TryGetValue(guildId, out var guild) ? guild.PremiumTier : 0;
        return kind == ExpressionKinds.Emoji
            ? tier switch { 1 => 100, 2 => 150, 3 => 250, _ => 50 }
            : tier switch { 1 => 15, 2 => 30, 3 => 60, _ => 5 };
    }

    private async Task<List<RankedItem>> RankedAsync(BotDbContext db, ulong guildId, string kind, ExpressionRules rules, Func<ulong, bool> onDiscord, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var since = DateOnly.FromDateTime((now - TimeSpan.FromDays(rules.MostUsedDays)).UtcDateTime);
        var trialSince = now - TimeSpan.FromDays(rules.TrialDays);
        var rows = await db.Expressions.AsNoTracking()
            .Where(e => e.GuildId == guildId && e.Kind == kind && e.State == ExpressionStates.Live)
            .Select(e => new { e.Id, e.Name, e.Pinned, e.DiscordId, e.DiscordRemovedAt, e.DecidedAt })
            .ToListAsync(ct);
        var ids = rows.Select(r => r.Id).ToList();
        var uses = await db.ExpressionUseDays.Where(u => ids.Contains(u.ExpressionId) && u.Day >= since)
            .GroupBy(u => u.ExpressionId).Select(g => new { g.Key, Count = g.Sum(u => u.Count) }).ToDictionaryAsync(g => g.Key, g => g.Count, ct);
        return [.. rows.Select(r => new RankedItem(r.Name, new SlotItem(r.Id, r.Pinned, r.DiscordId is { } id && onDiscord(id), true,
            r.DiscordRemovedAt is null, uses.GetValueOrDefault(r.Id), rules.TrialDays > 0 && r.DecidedAt >= trialSince)))];
    }

    // Discord's lists kept: pinned ones on them, then (most used) trial and the most used ones, swapping only for clearly
    // more uses; (managers) only what's pinned goes on. With only: just that one's place is made.
    private async Task KeepListAsync(ulong guildId, ExpressionRules rules, long? only = null, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var emojis = await rest.GetGuildEmojisAsync(guildId, cancellationToken: ct);
        var animated = (await db.Expressions.Where(e => e.GuildId == guildId && e.Kind == ExpressionKinds.Emoji && e.State == ExpressionStates.Live && e.Animated)
            .Select(e => e.Id).ToListAsync(ct)).ToHashSet();
        var ranked = await RankedAsync(db, guildId, ExpressionKinds.Emoji, rules, emojis.Select(e => e.Id).ToHashSet().Contains, ct);
        var swaps = 0;
        foreach (var moving in new[] { false, true })
            swaps += await KeepAsync(db, guildId, rules, [.. ranked.Where(r => animated.Contains(r.Item.Id) == moving)], Capacity(guildId, ExpressionKinds.Emoji) - emojis.Count(e => e.Animated == moving), _emojiSwaps, only, ct);
        if (only is null)
            _emojiSwaps.Made(guildId, swaps);

        var stickers = await rest.GetGuildStickersAsync(guildId, cancellationToken: ct);
        ranked = await RankedAsync(db, guildId, ExpressionKinds.Sticker, rules, stickers.Select(s => s.Id).ToHashSet().Contains, ct);
        swaps = await KeepAsync(db, guildId, rules, ranked, Capacity(guildId, ExpressionKinds.Sticker) - stickers.Count, _stickerSwaps, only, ct);
        if (only is null)
            _stickerSwaps.Made(guildId, swaps);
    }

    // One list kept by the policy; the swaps made.
    private async Task<int> KeepAsync(BotDbContext db, ulong guildId, ExpressionRules rules, IReadOnlyList<RankedItem> ranked, int free, DailySwaps daily, long? only, CancellationToken ct)
    {
        var mostUsed = rules.EmojiList == EmojiListChoices.MostUsed;
        var items = ranked.Select(r => r.Item).Where(i => (only is null || i.Id == only || i.OnDiscord) && (mostUsed || i.Pinned || i.OnDiscord)).ToList();
        var plan = SlotPolicy.Plan(items, Math.Max(0, free), only is not null ? 1 : mostUsed ? daily.Left(guildId, rules.SwapsPerDay) : 0, rules.SwapMargin);
        var swaps = 0;
        foreach (var item in await db.Expressions.Where(e => plan.Pull.Contains(e.Id)).ToListAsync(ct))
        {
            if (!await PullAsync(db, item))
                break;
            swaps++;
            logger.LogInformation("Took {Kind} {Name} off Discord's list of {GuildId} for a more used one", item.Kind, item.Name, guildId);
        }
        foreach (var item in await db.Expressions.Where(e => plan.Push.Contains(e.Id) && e.DiscordId == null).ToListAsync(ct))
        {
            if (!await PushAsync(db, item))
                break;
            logger.LogInformation("Put {Kind} {Name} on Discord's list of {GuildId}", item.Kind, item.Name, guildId);
        }
        return swaps;
    }

    private async Task<bool> PullAsync(BotDbContext db, Expression item)
    {
        if (item.DiscordId is not { } discordId)
            return true;
        try
        {
            if (item.Kind == ExpressionKinds.Emoji)
                await rest.DeleteGuildEmojiAsync(item.GuildId, discordId);
            else
                await rest.DeleteGuildStickerAsync(item.GuildId, discordId);
        }
        catch (RestException ex) when (ex.StatusCode != HttpStatusCode.NotFound)
        {
            logger.LogWarning("Taking {Kind} {Name} off Discord's list of {GuildId} failed: {Message}", item.Kind, item.Name, item.GuildId, ex.Message);
            return false;
        }
        _live.TryRemove(discordId, out _);
        item.DiscordId = null;
        await db.SaveChangesAsync();
        return true;
    }

    private async Task<bool> PushAsync(BotDbContext db, Expression item)
    {
        try
        {
            ulong made;
            if (item.Kind == ExpressionKinds.Emoji)
                made = (await rest.CreateGuildEmojiAsync(item.GuildId, new(item.Name, new ImageProperties((ImageFormat)Format(item.Kind, item.ContentType)!, item.Image)))).Id;
            else
            {
                var file = new AttachmentProperties(item.Name, new MemoryStream(item.Image)) { Description = "Made by a member" };
                made = (await rest.CreateGuildStickerAsync(item.GuildId, new(file, (StickerFormat)Format(item.Kind, item.ContentType)!, [item.Tag ?? "⭐"]))).Id;
            }
            item.DiscordId = made;
            _live[made] = (item.Id, item.GuildId, item.CreatorId);
            await db.SaveChangesAsync();
            return true;
        }
        catch (RestException ex)
        {
            logger.LogWarning("Putting {Kind} {Name} on Discord's list of {GuildId} failed: {Message}", item.Kind, item.Name, item.GuildId, ex.Message);
            return false;
        }
    }

    // Each library emoji as the bot's own, a few at a time; the bot's copy of one no longer in the library removed.
    private async Task KeepAppEmojiAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var missing = await db.Expressions
            .Where(e => e.Kind == ExpressionKinds.Emoji && e.State == ExpressionStates.Live && e.AppEmojiId == null && e.Image.Length <= MaxEmojiBytes)
            .OrderBy(e => e.Id).Take(AppEmojiAtOnce).ToListAsync(ct);
        var stale = await db.Expressions.Where(e => e.AppEmojiId != null && e.State != ExpressionStates.Live).OrderBy(e => e.Id).Take(AppEmojiAtOnce).ToListAsync(ct);
        if (missing.Count + stale.Count == 0)
            return;
        var applicationId = await app.IdAsync();
        foreach (var gone in stale)
            await DropAppEmojiAsync(applicationId, gone);
        var taken = (await rest.GetApplicationEmojisAsync(applicationId, cancellationToken: ct)).Select(e => e.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var emoji in missing)
        {
            var name = AppEmojiName(emoji.Name, emoji.Id, taken);
            try
            {
                var made = await rest.CreateApplicationEmojiAsync(applicationId, new(name, new ImageProperties((ImageFormat)Format(emoji.Kind, emoji.ContentType)!, emoji.Image)), cancellationToken: ct);
                (emoji.AppEmojiId, emoji.AppEmojiName) = (made.Id, made.Name);
                _live[made.Id] = (emoji.Id, emoji.GuildId, emoji.CreatorId);
                taken.Add(made.Name);
            }
            catch (RestException ex)
            {
                // The bot's emoji are full (2,000), or Discord didn't take the image: it stays usable only on Discord's list.
                logger.LogWarning("Making :{Name}: of {GuildId} the bot's own failed: {Message}", emoji.Name, emoji.GuildId, ex.Message);
                break;
            }
        }
        await db.SaveChangesAsync(ct);
    }

    private async Task DropAppEmojiAsync(ulong applicationId, Expression emoji)
    {
        if (emoji.AppEmojiId is not { } appEmojiId)
            return;
        try
        {
            await rest.DeleteApplicationEmojiAsync(applicationId, appEmojiId);
        }
        catch (RestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
        }
        _live.TryRemove(appEmojiId, out _);
        (emoji.AppEmojiId, emoji.AppEmojiName) = (null, null);
    }

    // The bot's emoji share one list across every server: a name already there gets the emoji's id.
    public static string AppEmojiName(string name, long id, IReadOnlySet<string> taken)
    {
        if (!taken.Contains(name))
            return name;
        var suffix = $"_{id}";
        return name[..Math.Min(name.Length, 32 - suffix.Length)] + suffix;
    }
}
