using System.Collections.Concurrent;
using System.Net;
using System.Text.RegularExpressions;

using Microsoft.EntityFrameworkCore;

using NetCord;
using NetCord.Gateway;
using NetCord.Rest;

using THOBOTTO.Data;
using THOBOTTO.Happenings;
using THOBOTTO.Modules;
using THOBOTTO.Notifications;
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
    Notifier notifier,
    HappeningForum forum,
    [FromKeyedServices(nameof(ExpressionShelf))] HttpClient http,
    TimeProvider time,
    ILogger<ExpressionShelf> logger) : BackgroundService
{
    public const string ModuleId = "emojis";

    private const int MaxEmojiBytes = 256 * 1024;
    private const int MaxStickerBytes = 512 * 1024;

    private static readonly TimeSpan Sweep = TimeSpan.FromMinutes(1);

    private readonly SemaphoreSlim _gate = new(1, 1);

    // Live expressions by their Discord id, for counting uses.
    private readonly ConcurrentDictionary<ulong, (long Id, ulong GuildId, ulong CreatorId)> _live = new();

    private readonly Lock _usage = new();
    private readonly Dictionary<long, (int Count, DateTimeOffset Last)> _uses = [];
    private readonly Dictionary<(ulong GuildId, ulong CreatorId), int> _royaltyUses = [];
    private readonly Dictionary<(ulong GuildId, ulong CreatorId), (DateOnly Day, double Paid)> _royaltiesToday = [];

    public async Task<string> ProposeAsync(ulong guildId, string kind, string name, string? tag, ulong proposerId, Attachment image, ulong channelId)
    {
        var limit = kind == ExpressionKinds.Emoji ? MaxEmojiBytes : MaxStickerBytes;
        if (image.Size > limit)
            return $"That file is {image.Size / 1024} KB; {kind}s can be at most {limit / 1024} KB.";
        if (Format(kind, image.ContentType) is null)
            return kind == ExpressionKinds.Emoji ? "Emojis must be PNG, JPEG, GIF or WebP." : "Stickers must be PNG (or APNG) or GIF.";
        return await ProposeAsync(guildId, kind, name, tag, proposerId, await http.GetByteArrayAsync(image.Url), image.ContentType!, channelId);
    }

    // The same from a file, from where it was proposed: a channel, or none (the panel).
    public async Task<string> ProposeAsync(ulong guildId, string kind, string name, string? tag, ulong proposerId, byte[] image, string contentType, ulong? channelId)
    {
        var rules = await settings.GetAsync<ExpressionRules>(guildId, ModuleId);
        if (!ValidName(kind, name))
            return kind == ExpressionKinds.Emoji
                ? "Emoji names are 2 to 32 letters, digits or underscores."
                : "Sticker names are 2 to 30 characters.";
        if (Format(kind, contentType) is null)
            return kind == ExpressionKinds.Emoji ? "Emojis must be PNG, JPEG, GIF or WebP." : "Stickers must be PNG (or APNG) or GIF.";
        var limit = kind == ExpressionKinds.Emoji ? MaxEmojiBytes : MaxStickerBytes;
        if (image.Length > limit)
            return $"That file is {image.Length / 1024} KB; {kind}s can be at most {limit / 1024} KB.";
        return await OpenAsync(guildId, kind, name, tag, creatorId: proposerId, proposerId, revivedFromId: null, image, contentType, rules, channelId);
    }

    public async Task<string> ReviveAsync(ulong guildId, string kind, string name, ulong reviverId, ulong channelId)
    {
        var rules = await settings.GetAsync<ExpressionRules>(guildId, ModuleId);

        await using var db = await dbFactory.CreateDbContextAsync();
        var retired = await db.Expressions
            .Where(e => e.GuildId == guildId && e.Kind == kind && e.Name == name && e.State == ExpressionStates.Retired)
            .OrderByDescending(e => e.RetiredAt)
            .FirstOrDefaultAsync();
        if (retired is null)
            return $"There's no retired {kind} called `{name}`.";

        return await OpenAsync(guildId, kind, name, retired.Tag, retired.CreatorId, reviverId, retired.Id, retired.Image, retired.ContentType, rules, channelId);
    }

    public async Task<string> VoteAsync(long id, ulong userId, bool up)
    {
        await _gate.WaitAsync();
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            var expression = await db.Expressions.FindAsync(id);
            if (expression is null || expression.State != ExpressionStates.Voting)
                return Proposals.VotingEnded;
            if (userId == expression.ProposerId)
                return Proposals.OwnProposal;
            var rules = await settings.GetAsync<ExpressionRules>(expression.GuildId, ModuleId);
            if (!up && !rules.DownVotes)
                return Proposals.DownVotesOff;

            var vote = await db.ExpressionVotes.FindAsync(id, userId);
            if (vote is null)
                db.ExpressionVotes.Add(new() { ExpressionId = id, UserId = userId, Up = up });
            else
                vote.Up = up;
            await db.SaveChangesAsync();

            var (ups, downs) = await CountAsync(db, id);
            if (Proposals.Accepted(ups, downs, rules))
                await AcceptAsync(db, expression, rules);
            else
                await RenderAsync(db, expression, rules);
            await points.InteractAsync(expression.GuildId, userId, InteractionKind.Vote, $"expression {id}");

            return Proposals.VoteReply(up);
        }
        finally
        {
            _gate.Release();
        }
    }

    // Votes so far on everything voted on here, for the panel.
    public async Task<IReadOnlyDictionary<long, (int Ups, int Downs)>> VotesAsync(ulong guildId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var ids = await db.Expressions.Where(e => e.GuildId == guildId && e.State == ExpressionStates.Voting).Select(e => e.Id).ToListAsync();
        return await db.ExpressionVotes.Where(v => ids.Contains(v.ExpressionId)).GroupBy(v => v.ExpressionId)
            .Select(g => new { g.Key, Ups = g.Count(v => v.Up), Downs = g.Count(v => !v.Up) }).ToDictionaryAsync(g => g.Key, g => (g.Ups, g.Downs));
    }

    // One for the panel's image endpoint: any state, this server's.
    public async Task<Expression?> FindAsync(ulong guildId, long id)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Expressions.AsNoTracking().FirstOrDefaultAsync(e => e.GuildId == guildId && e.Id == id);
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
                await UpkeepAsync(stoppingToken);
                await SweepAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Sweeping emojis and stickers failed");
            }
        }
    }

    private async Task<string> OpenAsync(ulong guildId, string kind, string name, string? tag, ulong creatorId, ulong proposerId, long? revivedFromId, byte[] image, string contentType, ExpressionRules rules, ulong? channelId)
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
                VoteChannelId = channelId,
            };
            db.Expressions.Add(expression);
            await db.SaveChangesAsync();

            if (channelId is { } here)
            {
                var message = await rest.SendMessageAsync(here, new()
                {
                    Attachments = [new AttachmentProperties(FileName(expression), new MemoryStream(image))],
                    Embeds = [Embed(expression, 0, 0, rules)],
                    Components = [Buttons(expression, rules)],
                });
                expression.VoteMessageId = message.Id;
                await db.SaveChangesAsync();
            }
            var inForum = await forum.ShowAsync(guildId, HappeningKinds.Emoji, expression.Id, HappeningStates.Open, Card(expression, 0, 0, rules));
            return Proposals.UpForVote(kind, channelId is not null, inForum is not null);
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
        await notifier.NotifyAsync(expression.GuildId, NotificationTopics.EmojiDecisions, [expression.ProposerId],
            Proposals.Decided(expression.Kind, expression.Name, accepted: true, expression.State == ExpressionStates.Live ? " and is live" : ""), await VoteLinkAsync(expression));
    }

    private async Task<string?> VoteLinkAsync(Expression e)
        => e.VoteChannelId is { } c ? Notifier.Link(e.GuildId, c, e.VoteMessageId) : await forum.LinkAsync(e.GuildId, HappeningKinds.Emoji, e.Id);

    // Uploads a waiting expression if there's a slot (or makes one, if set to). False if it has to keep waiting.
    private async Task<bool> TryPlaceAsync(BotDbContext db, Expression expression, ExpressionRules rules)
    {
        if (!await HasSlotAsync(expression))
        {
            var leastUsed = rules.ReplaceLeastUsed
                ? await db.Expressions
                    .Where(e => e.GuildId == expression.GuildId && e.Kind == expression.Kind && e.Animated == expression.Animated && e.State == ExpressionStates.Live && !e.Adopted)
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
            logger.LogWarning(ex, "Discord refused {Kind} {Name} in {GuildId}", expression.Kind, expression.Name, expression.GuildId);
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
                await notifier.NotifyAsync(expression.GuildId, NotificationTopics.EmojiDecisions, [expression.ProposerId], Proposals.Decided(expression.Kind, expression.Name, accepted: false), await VoteLinkAsync(expression));
            }

            foreach (var expression in await db.Expressions.Where(e => e.State == ExpressionStates.Waiting).OrderBy(e => e.DecidedAt).ToListAsync(ct))
            {
                var rules = await settings.GetAsync<ExpressionRules>(expression.GuildId, ModuleId);
                if (await TryPlaceAsync(db, expression, rules))
                    await RenderAsync(db, expression, rules);
            }

            foreach (var expression in await db.Expressions.Where(e => e.State == ExpressionStates.Live && !e.Adopted).ToListAsync(ct))
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

    // Every copy of the card: where it was proposed, and in the forum.
    private async Task RenderAsync(BotDbContext db, Expression expression, ExpressionRules rules)
    {
        var (ups, downs) = await CountAsync(db, expression.Id);
        await forum.ShowAsync(expression.GuildId, HappeningKinds.Emoji, expression.Id, Proposals.HappeningState(expression.State == ExpressionStates.Voting), Card(expression, ups, downs, rules));
        if (expression.VoteChannelId is not { } channelId || expression.VoteMessageId is not { } messageId)
            return;
        try
        {
            await rest.ModifyMessageAsync(channelId, messageId, m =>
            {
                m.Embeds = [Embed(expression, ups, downs, rules)];
                m.Components = [Buttons(expression, rules)];
            });
        }
        catch (RestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
        }
    }

    private static HappeningCard Card(Expression expression, int ups, int downs, ExpressionRules rules)
        => new($"{expression.Kind}: {expression.Name}", [Embed(expression, ups, downs, rules)], [Buttons(expression, rules)], (FileName(expression), expression.Image));

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
            ExpressionStates.Voting => Proposals.VotingStatus(ups, downs, rules, e.VoteEndsAt),
            ExpressionStates.Waiting => $"{Proposals.Outcome(true, ups, downs, rules)}; waiting for a free {e.Kind} slot.",
            ExpressionStates.Live => $"{Proposals.Outcome(true, ups, downs, rules)} and live.",
            ExpressionStates.Retired => "Accepted, later retired.",
            _ => $"{Proposals.Outcome(false, ups, downs, rules)}.",
        };

        return new()
        {
            Title = e.Kind == ExpressionKinds.Emoji ? $":{e.Name}:" : $"Sticker: {e.Name}",
            Description = $"{made}\n{status}",
            Image = $"attachment://{FileName(e)}",
            Color = e.State is ExpressionStates.Voting ? new(0xF1C40F) : e.State is ExpressionStates.Live or ExpressionStates.Waiting ? new(0x57F287) : new(0x99AAB5),
        };
    }

    private static ActionRowProperties Buttons(Expression e, ExpressionRules rules) => Proposals.Buttons("exvote", e.Id, e.State == ExpressionStates.Voting, rules);

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
