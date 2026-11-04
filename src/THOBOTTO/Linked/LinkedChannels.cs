using Microsoft.EntityFrameworkCore;

using NetCord;
using NetCord.Gateway;
using NetCord.Hosting.Gateway;
using NetCord.Rest;

using THOBOTTO.Access;
using THOBOTTO.Data;
using THOBOTTO.Discord;
using THOBOTTO.Modules;
using THOBOTTO.Notifications;
using THOBOTTO.Panel;
using THOBOTTO.Reposts;
using THOBOTTO.Translation;

namespace THOBOTTO.Linked;

// Two channels whose messages are copied to each other (or one way), in one server or two the bot is in; each side may
// have its copies translated into a language.
public sealed class ChannelLink
{
    public long Id { get; init; }

    public ulong GuildId { get; init; }

    public ulong ChannelId { get; init; }

    public string? Language { get; init; }

    public ulong OtherGuildId { get; init; }

    public ulong OtherChannelId { get; init; }

    public string? OtherLanguage { get; init; }

    // Both ways; otherwise only from the first channel to the other.
    public bool Both { get; init; } = true;

    public ulong CreatedById { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
}

public static class CopyCredits
{
    public const string Members = "members";
    public const string Always = "always";
    public const string Never = "never";
}

// Stored with SettingsStore under the module id: this server's, for the copies that come to it.
public sealed record LinkedRules
{
    [Setting("Reactions on copies from other servers count for their writer", Help = "Points and the hall of fame here. A copy from a channel of this same server always counts.",
        Choices = [CopyCredits.Members + "=Only when the writer is a member here too", CopyCredits.Always + "=Always", CopyCredits.Never + "=Never"])]
    public string CopyCredit { get; init; } = CopyCredits.Members;
}

// A copy of a message in a linked channel, so edits and removals follow it.
public sealed class LinkedCopy
{
    public ulong SourceId { get; init; }

    public ulong CopyId { get; init; }

    public string? Language { get; init; }
}

// Where messages from a channel go.
public sealed record LinkTarget(ulong GuildId, ulong ChannelId, string? Language);

public sealed class LinkedChannels(
    IDbContextFactory<BotDbContext> dbFactory,
    AsMember asMember,
    Reposter reposter,
    Translator translator,
    SettingsStore settings,
    ModuleState modules,
    PanelAccess members,
    GatewayClient gateway,
    [FromKeyedServices(nameof(LinkedChannels))] HttpClient http,
    TimeProvider time,
    ILogger<LinkedChannels> logger) : IHostedService
{
    public const string ModuleId = "linked-channels";
    public const int Most = 10;

    private volatile IReadOnlyDictionary<ulong, IReadOnlyList<LinkTarget>> _from = new Dictionary<ulong, IReadOnlyList<LinkTarget>>();

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await ReloadAsync();
        asMember.Posted += OnPostedAsync;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        asMember.Posted -= OnPostedAsync;
        return Task.CompletedTask;
    }

    // The links a server is on either side of.
    public async Task<IReadOnlyList<ChannelLink>> OfAsync(ulong guildId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.ChannelLinks.AsNoTracking().Where(l => l.GuildId == guildId || l.OtherGuildId == guildId).OrderBy(l => l.Id).ToListAsync();
    }

    // Null once linked, else why not.
    public async Task<string?> AddAsync(ChannelLink link)
    {
        if (link.ChannelId == link.OtherChannelId)
            return "A channel can't be linked to itself.";
        await using var db = await dbFactory.CreateDbContextAsync();
        if (await db.ChannelLinks.AnyAsync(l => (l.ChannelId == link.ChannelId && l.OtherChannelId == link.OtherChannelId) || (l.ChannelId == link.OtherChannelId && l.OtherChannelId == link.ChannelId)))
            return "Those two are linked already.";
        if (await db.ChannelLinks.CountAsync(l => l.GuildId == link.GuildId || l.OtherGuildId == link.GuildId) >= Most)
            return $"This server has {Most} links: remove one first.";
        db.ChannelLinks.Add(link);
        db.AuditEntries.Add(new() { GuildId = link.GuildId, ActorId = link.CreatedById, Action = "channels.link", Details = $"{link.ChannelId} {(link.Both ? "↔" : "→")} {link.OtherChannelId} ({link.OtherGuildId})", CreatedAt = time.GetUtcNow() });
        await db.SaveChangesAsync();
        await ReloadAsync();
        return null;
    }

    public async Task<bool> RemoveAsync(ulong guildId, long id, ulong actorId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var removed = await db.ChannelLinks.Where(l => l.Id == id && (l.GuildId == guildId || l.OtherGuildId == guildId)).ExecuteDeleteAsync() > 0;
        if (removed)
        {
            db.AuditEntries.Add(new() { GuildId = guildId, ActorId = actorId, Action = "channels.unlink", Details = id.ToString(), CreatedAt = time.GetUtcNow() });
            await db.SaveChangesAsync();
            await ReloadAsync();
        }
        return removed;
    }

    private async Task ReloadAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var from = new Dictionary<ulong, List<LinkTarget>>();
        foreach (var link in await db.ChannelLinks.AsNoTracking().ToListAsync())
        {
            Add(link.ChannelId, new(link.OtherGuildId, link.OtherChannelId, link.OtherLanguage));
            if (link.Both)
                Add(link.OtherChannelId, new(link.GuildId, link.ChannelId, link.Language));
        }
        _from = from.ToDictionary(f => f.Key, f => (IReadOnlyList<LinkTarget>)f.Value);

        void Add(ulong channelId, LinkTarget target)
        {
            if (!from.TryGetValue(channelId, out var targets))
                from[channelId] = targets = [];
            targets.Add(target);
        }
    }

    // A member's own message, unless a rewrite posts it again (that post comes as Posted, and is copied instead).
    public async Task OnMessageAsync(Message message)
    {
        if (message is not { GuildId: { } guildId, WebhookId: null, Author: GuildUser { IsBot: false } member }
            || message.Type is not (MessageType.Default or MessageType.Reply)
            || !_from.TryGetValue(message.ChannelId, out var targets)
            || await reposter.RepostedAsync(message))
            return;
        var withUsername = (await settings.GetAsync<RepostRules>(guildId, Reposter.ModuleId)).ShowUsername;
        var text = message.MessageReference is { Type: MessageReferenceType.Reply } reply
            ? $"-# ↪ {Notifier.Link(guildId, reply.ChannelId, reply.MessageId)}\n{message.Content}"
            : message.Content;
        await CopyAsync(message, guildId, member.Id, false, targets, AsMember.NameOf(member, withUsername), AsMember.AvatarOf(member), text);
    }

    private async Task OnPostedAsync(PostedFor posted)
    {
        if (_from.TryGetValue(posted.Channel.Id, out var targets))
        {
            var author = posted.Message.Author;
            await CopyAsync(posted.Message, posted.Channel.GuildId, posted.UserId, posted.Anonymous, targets, author.Username, (author.GetAvatarUrl() ?? author.DefaultAvatarUrl).ToString(), posted.Message.Content);
        }
    }

    private async Task CopyAsync(RestMessage source, ulong guildId, ulong userId, bool anonymous, IReadOnlyList<LinkTarget> targets, string name, string avatar, string text)
    {
        try
        {
            if (!await modules.IsEnabledAsync(guildId, ModuleId) || !gateway.Cache.Guilds.TryGetValue(guildId, out var from))
                return;
            foreach (var target in targets)
            {
                if (!await modules.IsEnabledAsync(target.GuildId, ModuleId) || !gateway.Cache.Guilds.TryGetValue(target.GuildId, out var to)
                    || to.Channels.GetValueOrDefault(target.ChannelId) is not TextGuildChannel channel || !MayPost(to, channel))
                    continue;
                var files = new List<MemoryStream>();
                try
                {
                    var attachments = new List<AttachmentProperties>();
                    foreach (var file in source.Attachments)
                    {
                        files.Add(new MemoryStream(await http.GetByteArrayAsync(file.Url)));
                        attachments.Add(new AttachmentProperties(file.FileName, files[^1]) { Description = file.Description });
                    }
                    var said = await TranslatedAsync(from, text, target.Language);
                    if (said.Length == 0 && attachments.Count == 0)
                        continue;
                    var (copy, _) = await asMember.PostForAsync(channel, to.Id, userId, new()
                    {
                        Content = said.Length > 2000 ? said[..1999] + "…" : said,
                        Username = to.Id == from.Id ? name : $"{name} · {from.Name}",
                        AvatarUrl = avatar,
                        Attachments = attachments,
                    }, anonymous, announce: false, credited: to.Id == from.Id || await CountsHereAsync(to.Id, userId));
                    if (copy is null)
                        continue;
                    await using var db = await dbFactory.CreateDbContextAsync();
                    db.LinkedCopies.Add(new() { SourceId = source.Id, CopyId = copy.Id, Language = target.Language });
                    await db.SaveChangesAsync();
                }
                finally
                {
                    foreach (var file in files)
                        await file.DisposeAsync();
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Copying message {MessageId} to its linked channels failed", source.Id);
        }
    }

    // An edit followed in the copies (an edit has a time; Discord filling in a link's preview doesn't).
    public async Task OnUpdatedAsync(Message message)
    {
        if (message.EditedAt is null || message.GuildId is not { } guildId || !gateway.Cache.Guilds.TryGetValue(guildId, out var from))
            return;
        await using var db = await dbFactory.CreateDbContextAsync();
        foreach (var copy in await db.LinkedCopies.AsNoTracking().Where(c => c.SourceId == message.Id).ToListAsync())
        {
            if (await asMember.FindAsync(copy.CopyId) is { } post)
                await asMember.EditAsync(post, await TranslatedAsync(from, message.Content, copy.Language));
        }
    }

    // A removal followed in the copies.
    public async Task OnDeletedAsync(ulong messageId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var copies = await db.LinkedCopies.Where(c => c.SourceId == messageId).ToListAsync();
        foreach (var copy in copies)
        {
            if (await asMember.FindAsync(copy.CopyId) is { } post)
                await asMember.DeleteAsync(post);
        }
        if (copies.Count > 0)
            await db.LinkedCopies.Where(c => c.SourceId == messageId).ExecuteDeleteAsync();
    }

    // In the target's language, with a line saying from what; as written when it's in that language already, or
    // translation isn't to be had.
    private async Task<string> TranslatedAsync(Guild from, string text, string? language)
    {
        if (language is null || string.IsNullOrWhiteSpace(text))
            return text;
        var (done, _) = await translator.TranslateAsync(text, id => from.Users.TryGetValue(id, out var u) ? u.Nickname ?? u.GlobalName ?? u.Username : null,
            (languages, _) => languages.FirstOrDefault(l => l.Code == language));
        return done is null ? text : $"{done.Text}\n-# 🌐 {done.From.Name} → {done.To.Name}";
    }

    private async Task<bool> CountsHereAsync(ulong guildId, ulong userId) => (await settings.GetAsync<LinkedRules>(guildId, ModuleId)).CopyCredit switch
    {
        CopyCredits.Always => true,
        CopyCredits.Never => false,
        _ => await members.InAsync(guildId, userId) is not null,
    };

    private bool MayPost(Guild guild, TextGuildChannel channel)
    {
        if (!guild.Users.TryGetValue(gateway.Id, out var bot))
            return false;
        var allowed = bot.GetChannelPermissions(guild, channel.Id);
        return allowed.HasFlag(Permissions.Administrator) || allowed.HasFlag(Permissions.ManageWebhooks);
    }
}

public sealed class LinkedHandlers(LinkedChannels linked) : IMessageCreateGatewayHandler, IMessageUpdateGatewayHandler, IMessageDeleteGatewayHandler
{
    ValueTask IMessageCreateGatewayHandler.HandleAsync(Message arg) => new(linked.OnMessageAsync(arg));

    ValueTask IMessageUpdateGatewayHandler.HandleAsync(Message arg) => new(linked.OnUpdatedAsync(arg));

    ValueTask IMessageDeleteGatewayHandler.HandleAsync(MessageDeleteEventArgs arg) => new(linked.OnDeletedAsync(arg.MessageId));
}

public sealed class LinkedPostings(LinkedChannels linked) : IPostingSource
{
    public async Task<IReadOnlyList<Posting>> PostingsAsync(Guild guild)
    {
        var postings = new List<Posting>();
        foreach (var link in await linked.OfAsync(guild.Id))
        {
            var setAt = $"/g/{guild.Id}/linked";
            if (link.GuildId == guild.Id && link.Both)
                postings.Add(new(LinkedChannels.ModuleId, "Messages from its linked channel", new Landing.In(link.ChannelId), setAt));
            if (link.OtherGuildId == guild.Id)
                postings.Add(new(LinkedChannels.ModuleId, "Messages from its linked channel", new Landing.In(link.OtherChannelId), setAt));
        }
        return postings;
    }
}

public static class LinkedStartup
{
    public static IServiceCollection AddLinked(this IServiceCollection services)
    {
        services.AddHttpClient(nameof(LinkedChannels), c => c.Timeout = TimeSpan.FromMinutes(1)).AddAsKeyed(ServiceLifetime.Singleton);
        return services
            .AddSingleton<LinkedChannels>()
            .AddHostedService(s => s.GetRequiredService<LinkedChannels>())
            .AddSingleton<IPostingSource, LinkedPostings>()
            .AddSingleton(new GuildNavEntry("linked", "Linked channels", GuildNavGroup.Community, 95, BotPermissions.ManageModules, LinkedChannels.ModuleId));
    }
}
