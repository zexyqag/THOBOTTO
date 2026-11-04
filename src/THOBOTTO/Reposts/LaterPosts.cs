using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using NetCord;
using NetCord.Gateway;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;
using NetCord.Services.ComponentInteractions;

using NodaTime;

using THOBOTTO.Data;
using THOBOTTO.Discord;
using THOBOTTO.Events;
using THOBOTTO.Members;
using THOBOTTO.Modules;
using THOBOTTO.Panel;

namespace THOBOTTO.Reposts;

// A message a member wrote to be posted later in a channel, under their name.
public sealed class LaterPost
{
    public long Id { get; init; }

    public ulong GuildId { get; init; }

    public ulong ChannelId { get; init; }

    public ulong UserId { get; init; }

    public required string Text { get; init; }

    public string? FileName { get; init; }

    public byte[]? File { get; init; }

    public DateTimeOffset At { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
}

// Posts waiting for their time, then posted as the member would (their words rewritten as any post of theirs is), if
// they're still in the server and may still write there; otherwise they get the text back in a DM.
public sealed class LaterPosts(
    IDbContextFactory<BotDbContext> dbFactory,
    AsMember asMember,
    Reposter reposter,
    SettingsStore settings,
    ModuleState modules,
    PanelAccess members,
    GatewayClient gateway,
    RestClient rest,
    TimeProvider time,
    ILogger<LaterPosts> logger) : BackgroundService
{
    public const int MostWaiting = 25;
    public const int FileBytes = 8 * 1024 * 1024;

    // The new one's id; or why not.
    public async Task<(long? Id, string? Problem)> AddAsync(ulong guildId, ulong channelId, ulong userId, string text, DateTimeOffset at, string? fileName, byte[]? file)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        if (await db.LaterPosts.CountAsync(p => p.UserId == userId) >= MostWaiting)
            return (null, $"You already have {MostWaiting} posts waiting.");
        var post = new LaterPost { GuildId = guildId, ChannelId = channelId, UserId = userId, Text = text, FileName = fileName, File = file, At = at, CreatedAt = time.GetUtcNow() };
        db.LaterPosts.Add(post);
        await db.SaveChangesAsync();
        return (post.Id, null);
    }

    // A person's own waiting, soonest first, without their files.
    public async Task<IReadOnlyList<LaterPost>> OfAsync(ulong userId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.LaterPosts.AsNoTracking().Where(p => p.UserId == userId).OrderBy(p => p.At)
            .Select(p => new LaterPost { Id = p.Id, GuildId = p.GuildId, ChannelId = p.ChannelId, UserId = p.UserId, Text = p.Text, FileName = p.FileName, At = p.At, CreatedAt = p.CreatedAt })
            .ToListAsync();
    }

    public async Task<bool> CancelAsync(ulong userId, long id)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.LaterPosts.Where(p => p.Id == id && p.UserId == userId).ExecuteDeleteAsync() > 0;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30), time);
        while (await timer.WaitForNextTickAsync(ct))
        {
            try
            {
                await using var db = await dbFactory.CreateDbContextAsync(ct);
                foreach (var due in await db.LaterPosts.Where(p => p.At <= time.GetUtcNow()).OrderBy(p => p.At).ToListAsync(ct))
                {
                    if (await PostAsync(due) is { } problem)
                        await TellAsync(due, problem);
                    db.LaterPosts.Remove(due);
                    await db.SaveChangesAsync(ct);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Posting the posts that were waiting failed");
            }
        }
    }

    // Null once posted, else why not.
    private async Task<string?> PostAsync(LaterPost post)
    {
        if (!gateway.Cache.Guilds.TryGetValue(post.GuildId, out var guild)
            || (guild.Channels.GetValueOrDefault(post.ChannelId) as TextGuildChannel ?? guild.ActiveThreads.GetValueOrDefault(post.ChannelId)) is not { } channel)
            return "its channel is gone";
        if (!await modules.IsEnabledAsync(guild.Id, Reposter.ModuleId) || !(await settings.GetAsync<RepostRules>(guild.Id, Reposter.ModuleId)).Later)
            return "that server doesn't post for members later any more";
        if (await members.InAsync(guild.Id, post.UserId) is not { Member: var member })
            return "you're not in that server any more";
        var allowed = member.GetChannelPermissions(guild, channel is GuildThread thread ? thread.ParentId : channel.Id);
        if (!allowed.HasFlag(Permissions.Administrator) && !allowed.HasFlag(Permissions.ViewChannel | (channel is GuildThread ? Permissions.SendMessagesInThreads : Permissions.SendMessages)))
            return "you can't write there any more";
        var text = await reposter.RewriteAsync(guild, member, channel, post.Text);
        var withUsername = (await settings.GetAsync<RepostRules>(guild.Id, Reposter.ModuleId)).ShowUsername;
        using var file = post.File is null ? null : new MemoryStream(post.File);
        var (_, problem) = await asMember.PostAsync(channel, member, withUsername, new()
        {
            Content = text.Length > 2000 ? text[..1999] + "…" : text,
            Attachments = file is null ? null : [new AttachmentProperties(post.FileName ?? "file", file)],
        });
        return problem;
    }

    private async Task TellAsync(LaterPost post, string why)
    {
        try
        {
            var dm = await rest.GetDMChannelAsync(post.UserId);
            await dm.SendMessageAsync(new() { Content = $"Your post for <#{post.ChannelId}> wasn't posted: {why}. It said:\n{post.Text}", AllowedMentions = AllowedMentionsProperties.None });
        }
        catch (RestException ex)
        {
            logger.LogInformation("Telling {UserId} about their post that couldn't be posted failed: {Message}", post.UserId, ex.Message);
        }
    }
}

// /later: written now, posted here at a time, under the member's name.
public sealed class LaterCommand(LaterPosts later, TimeZones zones, SettingsStore settings, ModuleState modules, [FromKeyedServices(nameof(LaterPosts))] HttpClient http, TimeProvider time)
    : GuildCommandModule
{
    [SlashCommand("later", "Write a message now to be posted here at a time, under your name", Contexts = [InteractionContextType.Guild])]
    public async Task LaterAsync(
        [SlashCommandParameter(Description = "When, in your time: in 2h, tomorrow 9:00, fri 18:00", MaxLength = 50)] string when,
        [SlashCommandParameter(Description = "What to post", MaxLength = 2000)] string text,
        [SlashCommandParameter(Description = "A file with it, at most 8 MB")] Attachment? file = null)
    {
        if (!await modules.IsEnabledAsync(GuildId, Reposter.ModuleId) || !(await settings.GetAsync<RepostRules>(GuildId, Reposter.ModuleId)).Later)
        {
            await RespondAsync(InteractionCallback.Message(Replies.Ephemeral("This server doesn't post members' messages later.")));
            return;
        }
        await RespondAsync(InteractionCallback.DeferredMessage(MessageFlags.Ephemeral));
        var (zone, own) = await zones.ForAsync(GuildId, Context.User.Id);
        var parsed = WhenParser.Parse(when, zone, Instant.FromDateTimeOffset(time.GetUtcNow()));
        string reply;
        long? id = null;
        if (parsed.At is not { } at)
            reply = $"{parsed.Problem}\n{TimeZones.Hint(zone, own)}";
        else if (file is not null && file.Size > LaterPosts.FileBytes)
            reply = $"A file is at most {LaterPosts.FileBytes / 1024 / 1024} MB.";
        else
        {
            var bytes = file is null ? null : await http.GetByteArrayAsync(file.Url);
            (id, var problem) = await later.AddAsync(GuildId, Context.Channel.Id, Context.User.Id, text.Trim(), at.ToDateTimeOffset(), file?.FileName, bytes);
            reply = problem ?? $"Posting it here <t:{at.ToUnixTimeSeconds()}:F> (<t:{at.ToUnixTimeSeconds()}:R>), under your name. {TimeZones.Hint(zone, own)}";
        }
        await ModifyResponseAsync(m =>
        {
            m.Content = reply;
            if (id is { } waiting)
                m.Components = [new ActionRowProperties { new ButtonProperties($"latercancel:{waiting}", "Cancel", ButtonStyle.Secondary) }];
        });
    }
}

public sealed class LaterButtons(LaterPosts later) : ComponentInteractionModule<ButtonInteractionContext>
{
    [ComponentInteraction("latercancel")]
    public async Task<InteractionCallbackProperties> CancelAsync(long id)
    {
        var cancelled = await later.CancelAsync(Context.User.Id, id);
        return InteractionCallback.ModifyMessage(m => (m.Content, m.Components) = (cancelled ? "Cancelled: it won't be posted." : "That one is posted or cancelled already.", []));
    }
}

// The posts a member has waiting in a server.
public sealed class LaterPostsData(IDbContextFactory<BotDbContext> dbFactory) : IMemberData
{
    public string Name => "Posts waiting to be posted";

    public async Task<long> CountAsync(ulong guildId, ulong userId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.LaterPosts.LongCountAsync(p => p.GuildId == guildId && p.UserId == userId);
    }

    public async Task WriteAsync(ulong guildId, ulong userId, Utf8JsonWriter json)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        json.WriteStartArray();
        await foreach (var post in db.LaterPosts.AsNoTracking().Where(p => p.GuildId == guildId && p.UserId == userId).OrderBy(p => p.At).Select(p => new { p.ChannelId, p.At, p.Text }).AsAsyncEnumerable())
        {
            json.WriteStartObject();
            json.WriteId("channel", post.ChannelId);
            json.WriteString("at", post.At);
            json.WriteString("text", post.Text);
            json.WriteEndObject();
        }
        json.WriteEndArray();
    }
}
