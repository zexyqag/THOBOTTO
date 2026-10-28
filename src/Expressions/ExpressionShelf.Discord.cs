using System.Collections.Concurrent;

using Microsoft.EntityFrameworkCore;

using NetCord;

namespace THOBOTTO.Expressions;

// Discord's own emoji and sticker lists as a view of the shelf: what members upload there comes in with its
// uploader as maker (so it earns royalties and counts), and one deleted there is marked retired. Looked at a
// moment after Discord says they changed, and every quarter hour.
public sealed partial class ExpressionShelf
{
    private static readonly TimeSpan Upkeep = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan SettleAfterChange = TimeSpan.FromSeconds(3);
    private DateTimeOffset _upkeepAt = DateTimeOffset.MinValue;
    private readonly ConcurrentDictionary<ulong, byte> _changed = new();

    // Discord said its emojis or stickers changed; a moment later (several events come at once) the shelf follows.
    public void DiscordChanged(ulong guildId)
    {
        if (!_changed.TryAdd(guildId, 0))
            return;
        _ = Task.Run(async () =>
        {
            await Task.Delay(SettleAfterChange, time);
            _changed.TryRemove(guildId, out _);
            try
            {
                if (await FollowsDiscordAsync(guildId))
                    await ReconcileAsync(guildId);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Following Discord's emoji change in {GuildId} failed", guildId);
            }
        });
    }

    private async Task<bool> FollowsDiscordAsync(ulong guildId)
        => await modules.IsEnabledAsync(guildId, ModuleId) && (await settings.GetAsync<ExpressionRules>(guildId, ModuleId)).FollowDiscord;

    private async Task UpkeepAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        if (now - _upkeepAt < Upkeep)
            return;
        _upkeepAt = now;
        foreach (var guildId in gateway.Cache.Guilds.Keys)
        {
            try
            {
                if (await FollowsDiscordAsync(guildId))
                    await ReconcileAsync(guildId, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Looking at Discord's emojis and stickers of {GuildId} failed", guildId);
            }
        }
    }

    // Discord's lists and the shelf brought in step: what members made there and the shelf doesn't know is adopted,
    // what the shelf has live but is gone there is retired (the image kept, so it can be revived).
    public async Task ReconcileAsync(ulong guildId, CancellationToken ct = default)
    {
        var onDiscord = new HashSet<ulong>();
        var adoptable = new List<DiscordExpression>();
        foreach (var e in await rest.GetGuildEmojisAsync(guildId, cancellationToken: ct))
        {
            onDiscord.Add(e.Id);
            if (e.Managed != true && e.Available != false && e.Creator?.Id != gateway.Id)
                adoptable.Add(new(e.Id, ExpressionKinds.Emoji, e.Name, null, e.Creator?.Id, e.Animated, e.Animated ? "image/gif" : "image/png", Snowflake.Timestamp(e.Id)));
        }
        foreach (var s in await rest.GetGuildStickersAsync(guildId, cancellationToken: ct))
        {
            onDiscord.Add(s.Id);
            if (s.Available != false && s.Creator?.Id != gateway.Id && s.Format != StickerFormat.Lottie)
                adoptable.Add(new(s.Id, ExpressionKinds.Sticker, s.Name, s.Tags.FirstOrDefault(), s.Creator?.Id, s.Format == StickerFormat.Gif,
                    s.Format switch { StickerFormat.Gif => "image/gif", StickerFormat.APng => "image/apng", _ => "image/png" }, s.CreatedAt));
        }

        await _gate.WaitAsync(ct);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var rows = await db.Expressions.Where(e => e.GuildId == guildId && e.DiscordId != null).ToListAsync(ct);
            var plan = ExpressionSync.Plan(onDiscord, adoptable, rows);
            var now = time.GetUtcNow();
            foreach (var expression in rows.Where(e => plan.Gone.Contains(e.Id)))
            {
                _live.TryRemove(expression.DiscordId!.Value, out _);
                expression.State = ExpressionStates.Retired;
                expression.RetiredAt = now;
                expression.DiscordId = null;
            }
            await db.SaveChangesAsync(ct);

            var owner = gateway.Cache.Guilds.TryGetValue(guildId, out var guild) ? guild.OwnerId : 0;
            var adopted = 0;
            foreach (var found in plan.Unknown)
            {
                if (await DownloadAsync(found, ct) is not { } image)
                    continue;
                var expression = new Expression
                {
                    GuildId = guildId,
                    Kind = found.Kind,
                    Name = found.Name,
                    Tag = found.Tag,
                    CreatorId = found.CreatorId ?? owner,
                    ProposerId = found.CreatorId ?? owner,
                    Image = image,
                    ContentType = found.ContentType,
                    Animated = found.Animated,
                    State = ExpressionStates.Live,
                    Adopted = true,
                    ProposedAt = found.CreatedAt,
                    VoteEndsAt = found.CreatedAt,
                    DecidedAt = found.CreatedAt,
                    DiscordId = found.Id,
                    LiveAt = found.CreatedAt,
                    LastUsedAt = now,
                };
                db.Expressions.Add(expression);
                await db.SaveChangesAsync(ct);
                _live[found.Id] = (expression.Id, guildId, expression.CreatorId);
                adopted++;
            }
            if (adopted + plan.Gone.Count > 0)
                logger.LogInformation("Emojis and stickers of {GuildId} and Discord's: {Adopted} adopted, {Gone} gone there and retired", guildId, adopted, plan.Gone.Count);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<byte[]?> DownloadAsync(DiscordExpression found, CancellationToken ct)
    {
        var url = found.Kind == ExpressionKinds.Emoji ? $"https://cdn.discordapp.com/emojis/{found.Id}.{(found.Animated ? "gif" : "png")}"
            : found.Animated ? $"https://media.discordapp.net/stickers/{found.Id}.gif" : $"https://cdn.discordapp.com/stickers/{found.Id}.png";
        try
        {
            return await http.GetByteArrayAsync(url, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning("Fetching {Kind} {Name} ({Id}) from Discord's CDN failed: {Message}", found.Kind, found.Name, found.Id, ex.Message);
            return null;
        }
    }
}
