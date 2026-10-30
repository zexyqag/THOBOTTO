using System.Collections.Concurrent;

using Microsoft.EntityFrameworkCore;

using NetCord;
using NetCord.Gateway;
using NetCord.Rest;

using THOBOTTO.Data;
using THOBOTTO.Debugging;
using THOBOTTO.Discord;
using THOBOTTO.Modules;

namespace THOBOTTO.Live;

// Members going live, as their Discord status shows it (a Twitch or YouTube account connected to Discord): kept from
// start to end, announced where the server says, the announcement changed to how long once it's over.
public sealed class LiveWatch(
    IDbContextFactory<BotDbContext> dbFactory,
    ModuleState modules,
    SettingsStore settings,
    GatewayClient gateway,
    RestClient rest,
    TimeProvider time,
    ILogger<LiveWatch> logger) : IHostedService
{
    public const string ModuleId = "live";

    // A stream that stops and starts again this soon is the same one (a dropped connection).
    private static readonly TimeSpan Resumed = TimeSpan.FromMinutes(10);

    // Who's live, so the many presence updates of everyone else need nothing looked up.
    private readonly ConcurrentDictionary<(ulong GuildId, ulong UserId), long> _open = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        foreach (var s in await db.LiveStreams.Where(s => s.EndedAt == null).Select(s => new { s.GuildId, s.UserId, s.Id }).ToListAsync(cancellationToken))
            _open[(s.GuildId, s.UserId)] = s.Id;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task<IReadOnlyList<LiveStream>> LiveAsync(ulong guildId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.LiveStreams.AsNoTracking().Where(s => s.GuildId == guildId && s.EndedAt == null).OrderBy(s => s.StartedAt).ToListAsync();
    }

    public static UserActivity? Streaming(IEnumerable<UserActivity>? activities)
        => activities?.FirstOrDefault(a => a.Type == UserActivityType.Streaming && Uri.TryCreate(a.Url, UriKind.Absolute, out var url) && url.Scheme is "https" or "http");

    public async Task SeenAsync(ulong? guildId, ulong userId, IEnumerable<UserActivity>? activities)
    {
        if (guildId is not { } id)
            return;
        var streaming = Streaming(activities);
        if (streaming is null && !_open.ContainsKey((id, userId)))
            return;
        await _gate.WaitAsync();
        try
        {
            await ChangeAsync(id, userId, streaming);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task CatchUpAsync(Guild guild)
    {
        var streaming = guild.Presences.Values.Select(p => (p.User.Id, Activity: Streaming(p.Activities))).Where(p => p.Activity is not null).ToDictionary(p => p.Id, p => p.Activity);
        var users = streaming.Keys.Concat(_open.Keys.Where(k => k.GuildId == guild.Id).Select(k => k.UserId)).Distinct().ToList();
        await _gate.WaitAsync();
        try
        {
            foreach (var userId in users)
                await ChangeAsync(guild.Id, userId, streaming.GetValueOrDefault(userId));
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task ChangeAsync(ulong guildId, ulong userId, UserActivity? activity)
    {
        using var _ = logger.BeginScope(DebugLog.Guild(guildId));
        await using var db = await dbFactory.CreateDbContextAsync();
        var open = _open.TryGetValue((guildId, userId), out var openId) ? await db.LiveStreams.FindAsync(openId) : null;
        var now = time.GetUtcNow();
        if (activity is null)
        {
            _open.TryRemove((guildId, userId), out var _);
            if (open is null)
                return;
            open.EndedAt = now;
            await db.SaveChangesAsync();
            await ShowAsync(open);
            return;
        }
        if (open is not null)
        {
            if ((open.Url, open.Title, open.Game) == (activity.Url, activity.Details, activity.State))
                return;
            (open.Platform, open.Url, open.Title, open.Game) = (activity.Name, activity.Url!, activity.Details, activity.State);
            await db.SaveChangesAsync();
            await ShowAsync(open);
            return;
        }
        if (!await modules.IsEnabledAsync(guildId, ModuleId))
            return;
        var rules = await settings.GetAsync<LiveRules>(guildId, ModuleId);
        if (rules.StreamerRoleId is { } streamers
            && !(gateway.Cache.Guilds.TryGetValue(guildId, out var guild) && guild.Users.TryGetValue(userId, out var member) && member.RoleIds.Contains(streamers)))
            return;
        var stream = await db.LiveStreams.Where(s => s.GuildId == guildId && s.UserId == userId && s.EndedAt > now - Resumed).OrderByDescending(s => s.EndedAt).FirstOrDefaultAsync();
        var resumed = stream is not null;
        if (stream is null)
            db.LiveStreams.Add(stream = new() { GuildId = guildId, UserId = userId, Platform = activity.Name, Url = activity.Url!, StartedAt = now });
        (stream.EndedAt, stream.Platform, stream.Url, stream.Title, stream.Game) = (null, activity.Name, activity.Url!, activity.Details, activity.State);
        if (!resumed && rules.ChannelId is { } channelId)
        {
            try
            {
                var mention = rules.MentionRoleId is { } role ? $"<@&{role}> " : "";
                var message = await rest.SendMessageAsync(channelId, new()
                {
                    Content = mention + Text(stream),
                    AllowedMentions = rules.MentionRoleId is { } allowed ? new() { AllowedRoles = [allowed], AllowedUsers = [], Everyone = false } : AllowedMentionsProperties.None,
                });
                (stream.ChannelId, stream.MessageId) = (channelId, message.Id);
            }
            catch (RestException ex)
            {
                logger.LogWarning("Announcing <@{UserId}> live in {ChannelId} failed: {Message}", userId, channelId, ex.Message);
            }
        }
        await db.SaveChangesAsync();
        _open[(guildId, userId)] = stream.Id;
        if (resumed)
            await ShowAsync(stream);
    }

    private async Task ShowAsync(LiveStream stream)
    {
        if (stream is not { ChannelId: { } channelId, MessageId: { } messageId })
            return;
        try
        {
            await rest.ModifyMessageAsync(channelId, messageId, m => (m.Content, m.AllowedMentions) = (Text(stream), AllowedMentionsProperties.None));
        }
        catch (RestException ex)
        {
            logger.LogDebug("Changing the live announcement {MessageId} failed: {Message}", messageId, ex.Message);
        }
    }

    // Live: the link last, so Discord shows the stream's preview; over: how long, the link without a preview.
    public static string Text(LiveStream stream)
    {
        var what = $"{(stream.Title is { } title ? $": **{title}**" : "")}{(stream.Game is { } game ? $" · {game}" : "")}";
        return stream.EndedAt is { } ended
            ? $"⚫ <@{stream.UserId}> was live on {stream.Platform}{what} · {Durations.Format(ended - stream.StartedAt)}\n-# <{stream.Url}>"
            : $"🔴 <@{stream.UserId}> is live on {stream.Platform}{what}\n{stream.Url}";
    }
}
