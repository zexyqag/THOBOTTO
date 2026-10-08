using System.Collections.Concurrent;
using System.Net;
using System.Text.RegularExpressions;

using Microsoft.EntityFrameworkCore;

using NetCord;
using NetCord.Gateway;
using NetCord.Rest;

using THOBOTTO.Data;
using THOBOTTO.Modules;
using THOBOTTO.Points;

namespace THOBOTTO.Expressions;

// Member-made emojis and stickers: proposals and votes, uploading into free slots, usage and
// royalties, and retiring ones nobody uses. Changes run one at a time under a gate; usage is
// counted in memory and written by the sweep every minute.
public sealed partial class ExpressionShelf(
    RestClient rest,
    GatewayClient gateway,
    IDbContextFactory<BotDbContext> dbFactory,
    ModuleState modules,
    SettingsStore settings,
    PointsEngine points,
    TimeProvider time,
    ILogger<ExpressionShelf> logger) : BackgroundService
{
    public const string ModuleId = "emojis";

    private const int MaxEmojiBytes = 256 * 1024;
    private const int MaxStickerBytes = 512 * 1024;

    private static readonly TimeSpan Sweep = TimeSpan.FromMinutes(1);
    private static readonly HttpClient Http = new();

    private readonly SemaphoreSlim _gate = new(1, 1);

    // Live expressions by their Discord id, for counting uses.
    private readonly ConcurrentDictionary<ulong, (long Id, ulong GuildId, ulong CreatorId)> _live = new();

    private readonly Lock _usage = new();
    private readonly Dictionary<long, (int Count, DateTimeOffset Last)> _uses = [];
    private readonly Dictionary<(ulong GuildId, ulong CreatorId), int> _royaltyUses = [];
    private readonly Dictionary<(ulong GuildId, ulong CreatorId), (DateOnly Day, double Paid)> _royaltiesToday = [];

    public async Task<string> ProposeAsync(ulong guildId, string kind, string name, string? tag, ulong proposerId, Attachment image)
    {
        var rules = await settings.GetAsync<ExpressionRules>(guildId, ModuleId);
        if (rules.VoteChannelId is null)
            return "There's no vote channel yet; someone with `emojis.manage` can set one with `/emoji settings`.";
        if (!ValidName(kind, name))
            return kind == ExpressionKinds.Emoji
                ? "Emoji names are 2 to 32 letters, digits or underscores."
                : "Sticker names are 2 to 30 characters.";
        if (Format(kind, image.ContentType) is null)
            return kind == ExpressionKinds.Emoji ? "Emojis must be PNG, JPEG, GIF or WebP." : "Stickers must be PNG (or APNG) or GIF.";
        var limit = kind == ExpressionKinds.Emoji ? MaxEmojiBytes : MaxStickerBytes;
        if (image.Size > limit)
            return $"That file is {image.Size / 1024} KB; {kind}s can be at most {limit / 1024} KB.";

        var bytes = await Http.GetByteArrayAsync(image.Url);
        return await OpenAsync(guildId, kind, name, tag, creatorId: proposerId, proposerId, revivedFromId: null, bytes, image.ContentType!, rules);
    }

    public async Task<string> ReviveAsync(ulong guildId, string kind, string name, ulong reviverId)
    {
        var rules = await settings.GetAsync<ExpressionRules>(guildId, ModuleId);
        if (rules.VoteChannelId is null)
            return "There's no vote channel yet.";

        await using var db = await dbFactory.CreateDbContextAsync();
        var retired = await db.Expressions
            .Where(e => e.GuildId == guildId && e.Kind == kind && e.Name == name && e.State == ExpressionStates.Retired)
            .OrderByDescending(e => e.RetiredAt)
            .FirstOrDefaultAsync();
        if (retired is null)
            return $"There's no retired {kind} called `{name}`.";

        return await OpenAsync(guildId, kind, name, retired.Tag, retired.CreatorId, reviverId, retired.Id, retired.Image, retired.ContentType, rules);
    }

    public async Task<string> VoteAsync(long id, ulong userId, bool up)
    {
        await _gate.WaitAsync();
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            var expression = await db.Expressions.FindAsync(id);
            if (expression is null || expression.State != ExpressionStates.Voting)
                return "Voting on this has ended.";
            if (userId == expression.ProposerId)
                return "You put this one up, so your vote doesn't count.";

            var vote = await db.ExpressionVotes.FindAsync(id, userId);
            if (vote is null)
                db.ExpressionVotes.Add(new() { ExpressionId = id, UserId = userId, Up = up });
            else
                vote.Up = up;
            await db.SaveChangesAsync();

            var rules = await settings.GetAsync<ExpressionRules>(expression.GuildId, ModuleId);
            var (ups, downs) = await CountAsync(db, id);
            if (ups - downs >= rules.VoteMargin)
                await AcceptAsync(db, expression, rules);
            else
                await RenderAsync(db, expression, rules);

            return up ? "Voted 👍." : "Voted 👎.";
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<Expression>> ListAsync(ulong guildId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Expressions
            .Where(e => e.GuildId == guildId && (e.State == ExpressionStates.Live || e.State == ExpressionStates.Waiting || e.State == ExpressionStates.Voting || e.State == ExpressionStates.Retired))
            .OrderBy(e => e.Kind).ThenBy(e => e.State).ThenByDescending(e => e.Uses)
            .ToListAsync();
    }

    // A message from someone (not a bot): count each live custom emoji and sticker in it once.
    public async Task OnMessageAsync(ulong guildId, ulong userId, string content, IEnumerable<ulong> stickerIds)
    {
        if (_live.IsEmpty || !await modules.IsEnabledAsync(guildId, ModuleId))
            return;

        var ids = CustomEmoji().Matches(content).Select(m => ulong.Parse(m.Groups[1].Value)).Concat(stickerIds).Distinct();
        foreach (var id in ids)
            RecordUse(guildId, userId, id);
    }

    public async Task OnReactionAsync(ulong guildId, ulong userId, ulong emojiId)
    {
        if (!_live.IsEmpty && await modules.IsEnabledAsync(guildId, ModuleId))
            RecordUse(guildId, userId, emojiId);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await using (var db = await dbFactory.CreateDbContextAsync(stoppingToken))
        {
            foreach (var e in await db.Expressions.Where(e => e.State == ExpressionStates.Live && e.DiscordId != null).ToListAsync(stoppingToken))
                _live[e.DiscordId!.Value] = (e.Id, e.GuildId, e.CreatorId);
        }

        using var timer = new PeriodicTimer(Sweep, time);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await SweepAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Sweeping emojis and stickers failed");
            }
        }
    }

    private async Task<string> OpenAsync(ulong guildId, string kind, string name, string? tag, ulong creatorId, ulong proposerId, long? revivedFromId, byte[] image, string contentType, ExpressionRules rules)
    {
        await _gate.WaitAsync();
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            var taken = await db.Expressions.AnyAsync(e => e.GuildId == guildId && e.Kind == kind && e.Name == name
                && (e.State == ExpressionStates.Voting || e.State == ExpressionStates.Waiting || e.State == ExpressionStates.Live));
            if (taken)
                return $"There's already a {kind} called `{name}` up for a vote or in use.";

            var price = await points.ChargesAsync(guildId) ? rules.ProposeCost : 0;
            if (price > 0 && !await points.TrySpendAsync(guildId, proposerId, price, $"{kind} proposal {name}"))
                return $"Proposing costs {points.Rules(guildId).Format(price)} (refunded if it's accepted).";

            var now = time.GetUtcNow();
            var expression = new Expression
            {
                GuildId = guildId,
                Kind = kind,
                Name = name,
                Tag = tag,
                CreatorId = creatorId,
                ProposerId = proposerId,
                RevivedFromId = revivedFromId,
                Image = image,
                ContentType = contentType,
                Animated = contentType == "image/gif",
                Paid = price,
                ProposedAt = now,
                VoteEndsAt = now + TimeSpan.FromDays(rules.VoteDays),
                VoteChannelId = rules.VoteChannelId,
            };
            db.Expressions.Add(expression);
            await db.SaveChangesAsync();

            var message = await rest.SendMessageAsync(rules.VoteChannelId!.Value, new()
            {
                Attachments = [new AttachmentProperties(FileName(expression), new MemoryStream(image))],
                Embeds = [Embed(expression, 0, 0, rules)],
                Components = [Buttons(expression)],
            });
            expression.VoteMessageId = message.Id;
            await db.SaveChangesAsync();

            return $"Your {kind} is up for a vote in <#{rules.VoteChannelId}>.";
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task AcceptAsync(BotDbContext db, Expression expression, ExpressionRules rules)
    {
        expression.State = ExpressionStates.Waiting;
        expression.DecidedAt = time.GetUtcNow();
        await db.SaveChangesAsync();

        if (expression.Paid > 0)
            await points.RefundAsync(expression.GuildId, expression.ProposerId, expression.Paid, $"{expression.Kind} {expression.Name} accepted");
        if (expression.RevivedFromId is null && rules.AcceptBonus > 0 && await points.ChargesAsync(expression.GuildId))
            await points.AwardAsync(expression.GuildId, expression.CreatorId, rules.AcceptBonus, PointEntryKinds.Expression, $"{expression.Kind} {expression.Name} accepted");

        await TryPlaceAsync(db, expression, rules);
        await RenderAsync(db, expression, rules);
    }

    // Uploads a waiting expression if there's a slot (or makes one, if set to). False if it has to keep waiting.
    private async Task<bool> TryPlaceAsync(BotDbContext db, Expression expression, ExpressionRules rules)
    {
        if (!await HasSlotAsync(expression))
        {
            var leastUsed = rules.ReplaceLeastUsed
                ? await db.Expressions
                    .Where(e => e.GuildId == expression.GuildId && e.Kind == expression.Kind && e.Animated == expression.Animated && e.State == ExpressionStates.Live)
                    .OrderBy(e => e.Uses).ThenBy(e => e.LastUsedAt)
                    .FirstOrDefaultAsync()
                : null;
            if (leastUsed is null)
                return false;

            await RetireAsync(db, leastUsed);
        }

        try
        {
            ulong discordId;
            if (expression.Kind == ExpressionKinds.Emoji)
            {
                var format = (ImageFormat)Format(expression.Kind, expression.ContentType)!;
                discordId = (await rest.CreateGuildEmojiAsync(expression.GuildId, new(expression.Name, new ImageProperties(format, expression.Image, false)))).Id;
            }
            else
            {
                var format = (StickerFormat)Format(expression.Kind, expression.ContentType)!;
                var file = new AttachmentProperties(expression.Name, new MemoryStream(expression.Image)) { Description = "Made by a member" };
                discordId = (await rest.CreateGuildStickerAsync(expression.GuildId, new(file, format, [expression.Tag ?? "⭐"]))).Id;
            }

            var now = time.GetUtcNow();
            expression.State = ExpressionStates.Live;
            expression.DiscordId = discordId;
            expression.LiveAt = now;
            expression.LastUsedAt = now;
            await db.SaveChangesAsync();
            _live[discordId] = (expression.Id, expression.GuildId, expression.CreatorId);
            return true;
        }
        catch (RestException ex) when (ex.StatusCode == HttpStatusCode.BadRequest)
        {
            // Discord didn't take the image (size, dimensions); it can never go live.
            logger.LogWarning(ex, "Discord refused {Kind} {Name}", expression.Kind, expression.Name);
            expression.State = ExpressionStates.Rejected;
            await db.SaveChangesAsync();
            return false;
        }
    }

    private async Task<bool> HasSlotAsync(Expression expression)
    {
        var tier = gateway.Cache.Guilds.TryGetValue(expression.GuildId, out var guild) ? guild.PremiumTier : 0;
        if (expression.Kind == ExpressionKinds.Emoji)
        {
            var emojis = await rest.GetGuildEmojisAsync(expression.GuildId);
            var capacity = tier switch { 1 => 100, 2 => 150, 3 => 250, _ => 50 };
            return emojis.Count(e => e.Animated == expression.Animated) < capacity;
        }

        var stickers = await rest.GetGuildStickersAsync(expression.GuildId);
        return stickers.Count < tier switch { 1 => 15, 2 => 30, 3 => 60, _ => 5 };
    }

    private async Task RetireAsync(BotDbContext db, Expression expression)
    {
        if (expression.DiscordId is { } discordId)
        {
            try
            {
                if (expression.Kind == ExpressionKinds.Emoji)
                    await rest.DeleteGuildEmojiAsync(expression.GuildId, discordId);
                else
                    await rest.DeleteGuildStickerAsync(expression.GuildId, discordId);
            }
            catch (RestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
            }

            _live.TryRemove(discordId, out _);
        }

        expression.State = ExpressionStates.Retired;
        expression.RetiredAt = time.GetUtcNow();
        expression.DiscordId = null;
        await db.SaveChangesAsync();
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        await FlushUsageAsync(ct);

        await _gate.WaitAsync(ct);
        try
        {
            var now = time.GetUtcNow();
            await using var db = await dbFactory.CreateDbContextAsync(ct);

            foreach (var expression in await db.Expressions.Where(e => e.State == ExpressionStates.Voting && e.VoteEndsAt <= now).ToListAsync(ct))
            {
                expression.State = ExpressionStates.Rejected;
                expression.DecidedAt = now;
                await db.SaveChangesAsync(ct);
                await RenderAsync(db, expression, await settings.GetAsync<ExpressionRules>(expression.GuildId, ModuleId));
            }

            foreach (var expression in await db.Expressions.Where(e => e.State == ExpressionStates.Waiting).OrderBy(e => e.DecidedAt).ToListAsync(ct))
            {
                var rules = await settings.GetAsync<ExpressionRules>(expression.GuildId, ModuleId);
                if (await TryPlaceAsync(db, expression, rules))
                    await RenderAsync(db, expression, rules);
            }

            foreach (var expression in await db.Expressions.Where(e => e.State == ExpressionStates.Live).ToListAsync(ct))
            {
                var rules = await settings.GetAsync<ExpressionRules>(expression.GuildId, ModuleId);
                if (now - (expression.LastUsedAt ?? expression.LiveAt ?? now) >= TimeSpan.FromDays(rules.RetireAfterDays))
                    await RetireAsync(db, expression);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private void RecordUse(ulong guildId, ulong userId, ulong discordId)
    {
        if (!_live.TryGetValue(discordId, out var live) || live.GuildId != guildId)
            return;

        var now = time.GetUtcNow();
        lock (_usage)
        {
            _uses[live.Id] = (_uses.GetValueOrDefault(live.Id).Count + 1, now);
            if (userId != live.CreatorId)
                _royaltyUses[(guildId, live.CreatorId)] = _royaltyUses.GetValueOrDefault((guildId, live.CreatorId)) + 1;
        }
    }

    private async Task FlushUsageAsync(CancellationToken ct)
    {
        Dictionary<long, (int Count, DateTimeOffset Last)> uses;
        Dictionary<(ulong GuildId, ulong CreatorId), int> royaltyUses;
        lock (_usage)
        {
            uses = new(_uses);
            royaltyUses = new(_royaltyUses);
            _uses.Clear();
            _royaltyUses.Clear();
        }

        if (uses.Count > 0)
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            foreach (var (id, (count, last)) in uses)
                await db.Expressions.Where(e => e.Id == id)
                    .ExecuteUpdateAsync(s => s.SetProperty(e => e.Uses, e => e.Uses + count).SetProperty(e => e.LastUsedAt, last), ct);
        }

        var today = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);
        foreach (var ((guildId, creatorId), count) in royaltyUses)
        {
            if (!await points.ChargesAsync(guildId))
                continue;

            var rules = await settings.GetAsync<ExpressionRules>(guildId, ModuleId);
            var paid = _royaltiesToday.TryGetValue((guildId, creatorId), out var day) && day.Day == today ? day.Paid : 0;
            var amount = Math.Min(count * rules.RoyaltyPerUse, rules.RoyaltyDailyCap - paid);
            if (amount <= 0)
                continue;

            await points.AwardAsync(guildId, creatorId, amount, PointEntryKinds.Royalty, $"{count} uses of your emojis and stickers");
            _royaltiesToday[(guildId, creatorId)] = (today, paid + amount);
        }
    }

    private async Task RenderAsync(BotDbContext db, Expression expression, ExpressionRules rules)
    {
        if (expression.VoteChannelId is not { } channelId || expression.VoteMessageId is not { } messageId)
            return;

        var (ups, downs) = await CountAsync(db, expression.Id);
        try
        {
            await rest.ModifyMessageAsync(channelId, messageId, m =>
            {
                m.Embeds = [Embed(expression, ups, downs, rules)];
                m.Components = [Buttons(expression)];
            });
        }
        catch (RestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
        }
    }

    private static async Task<(int Ups, int Downs)> CountAsync(BotDbContext db, long id)
    {
        var votes = await db.ExpressionVotes.Where(v => v.ExpressionId == id).Select(v => v.Up).ToListAsync();
        return (votes.Count(up => up), votes.Count(up => !up));
    }

    private static EmbedProperties Embed(Expression e, int ups, int downs, ExpressionRules rules)
    {
        var made = e.RevivedFromId is null ? $"Made by <@{e.CreatorId}>" : $"Made by <@{e.CreatorId}>, revived by <@{e.ProposerId}>";
        var status = e.State switch
        {
            ExpressionStates.Voting => $"👍 {ups} · 👎 {downs}. Needs {rules.VoteMargin} more 👍 than 👎, voting ends <t:{e.VoteEndsAt.ToUnixTimeSeconds()}:R>.",
            ExpressionStates.Waiting => $"Accepted ({ups}–{downs}); waiting for a free {e.Kind} slot.",
            ExpressionStates.Live => $"Accepted ({ups}–{downs}) and live.",
            ExpressionStates.Retired => "Accepted, later retired.",
            _ => $"Not accepted ({ups}–{downs}).",
        };

        return new()
        {
            Title = e.Kind == ExpressionKinds.Emoji ? $":{e.Name}:" : $"Sticker: {e.Name}",
            Description = $"{made}\n{status}",
            Image = $"attachment://{FileName(e)}",
            Color = e.State is ExpressionStates.Voting ? new(0xF1C40F) : e.State is ExpressionStates.Live or ExpressionStates.Waiting ? new(0x57F287) : new(0x99AAB5),
        };
    }

    private static ActionRowProperties Buttons(Expression e)
    {
        var open = e.State == ExpressionStates.Voting;
        return new()
        {
            new ButtonProperties($"exvote:{e.Id}:1", EmojiProperties.Standard("👍"), ButtonStyle.Success) { Disabled = !open },
            new ButtonProperties($"exvote:{e.Id}:0", EmojiProperties.Standard("👎"), ButtonStyle.Danger) { Disabled = !open },
        };
    }

    private static string FileName(Expression e) => $"{e.Name}.{e.ContentType.Split('/')[1]}";

    private static bool ValidName(string kind, string name)
        => kind == ExpressionKinds.Emoji ? EmojiName().IsMatch(name) : name.Trim().Length is >= 2 and <= 30;

    // The upload format for a content type, or null if that kind can't use it.
    private static object? Format(string kind, string? contentType) => (kind, contentType) switch
    {
        (ExpressionKinds.Emoji, "image/png") => ImageFormat.Png,
        (ExpressionKinds.Emoji, "image/jpeg") => ImageFormat.Jpeg,
        (ExpressionKinds.Emoji, "image/gif") => ImageFormat.Gif,
        (ExpressionKinds.Emoji, "image/webp") => ImageFormat.Webp,
        (ExpressionKinds.Sticker, "image/png") => StickerFormat.Png,
        (ExpressionKinds.Sticker, "image/apng") => StickerFormat.APng,
        (ExpressionKinds.Sticker, "image/gif") => StickerFormat.Gif,
        _ => null,
    };

    [GeneratedRegex(@"<a?:\w+:(\d+)>")]
    private static partial Regex CustomEmoji();

    [GeneratedRegex(@"^[A-Za-z0-9_]{2,32}$")]
    private static partial Regex EmojiName();
}
