using System.Net;

using Microsoft.EntityFrameworkCore;

using NetCord;
using NetCord.Rest;

using THOBOTTO.Data;
using THOBOTTO.Debugging;
using THOBOTTO.Modules;
using THOBOTTO.Notifications;

namespace THOBOTTO.Happenings;

// What a happening shows: its title (the post's name), its card, and the file the card shows (attached when posted).
public sealed record HappeningCard(string Title, IReadOnlyList<EmbedProperties> Embeds, IEnumerable<IMessageComponentProperties> Components, (string Name, byte[] Bytes)? File = null);

// Happenings in the forum: each kind the server shows there gets a post whose first message is the same card as where
// it was started, redrawn with it, tagged by kind and state, archived once it's over and opened again if it comes
// back (a reverted bet).
public sealed class HappeningForum(
    IDbContextFactory<BotDbContext> dbFactory,
    SettingsStore settings,
    ModuleState modules,
    RestClient rest,
    TimeProvider time,
    ILogger<HappeningForum> logger)
{
    public const string ModuleId = "happenings";

    private const int TitleLength = 100;

    private static readonly IReadOnlyDictionary<string, (string Name, string Emoji)> Tags = new Dictionary<string, (string, string)>
    {
        [HappeningKinds.Event] = ("Event", "📅"),
        [HappeningKinds.Session] = ("Session", "🎮"),
        [HappeningKinds.Bet] = ("Bet", "🎲"),
        [HappeningKinds.Emoji] = ("Emoji", "😀"),
        [HappeningKinds.Sound] = ("Sound", "🔊"),
        [HappeningKinds.Wrapped] = ("Wrapped", "🎁"),
        [HappeningStates.Open] = ("Open", "🟢"),
        [HappeningStates.Done] = ("Done", "✅"),
        [HappeningStates.Cancelled] = ("Cancelled", "❌"),
    };

    private readonly SemaphoreSlim _gate = new(1, 1);

    // The happening's forum post, as a link, when it has one.
    public async Task<string?> LinkAsync(ulong guildId, string kind, long id)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.HappeningPosts.FirstOrDefaultAsync(p => p.GuildId == guildId && p.Kind == kind && p.ThingId == id && !p.Gone) is { } post ? Notifier.Link(guildId, post.ThreadId) : null;
    }

    public async Task<ulong?> ForumOfAsync(ulong guildId) => (await settings.GetAsync<HappeningRules>(guildId, ModuleId)).ForumChannelId;

    // Whether the server shows that kind of happening in its forum.
    public async Task<bool> ShowsAsync(ulong guildId, string kind)
        => await modules.IsEnabledAsync(guildId, ModuleId) && await settings.GetAsync<HappeningRules>(guildId, ModuleId) is { ForumChannelId: not null } rules && rules.Shows(kind);

    // Posts or redraws the happening's card in the forum, when the server shows that kind there; the post's id (its
    // card's too), or null.
    public async Task<ulong?> ShowAsync(ulong guildId, string kind, long id, string state, HappeningCard card)
    {
        if (!await ShowsAsync(guildId, kind))
            return null;
        var forumId = (await settings.GetAsync<HappeningRules>(guildId, ModuleId)).ForumChannelId!.Value;
        await _gate.WaitAsync();
        // What goes wrong shows on the server's Debug page.
        using var _ = logger.BeginScope(DebugLog.Guild(guildId));
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            var post = await db.HappeningPosts.FirstOrDefaultAsync(p => p.GuildId == guildId && p.Kind == kind && p.ThingId == id);
            if (post is { Gone: true })
                return null;
            try
            {
                var tags = await TagsAsync(forumId, kind, state);
                if (post is null || post.ForumId != forumId)
                {
                    // What's already over when the forum first hears of it stays where it was started.
                    if (HappeningStates.Over(state))
                        return null;
                    var thread = await rest.CreateForumGuildThreadAsync(forumId, new(Clip(card.Title), new()
                    {
                        Embeds = card.Embeds,
                        Components = card.Components,
                        Attachments = card.File is var (name, bytes) ? [new AttachmentProperties(name, new MemoryStream(bytes))] : [],
                        AllowedMentions = AllowedMentionsProperties.None,
                    })
                    { AppliedTags = tags });
                    if (post is null)
                        db.HappeningPosts.Add(post = new() { GuildId = guildId, Kind = kind, ThingId = id, CreatedAt = time.GetUtcNow() });
                    (post.ForumId, post.ThreadId, post.State) = (forumId, thread.Id, state);
                }
                else
                {
                    // An archived post's card can't be edited: it's opened first, and archived again when still over.
                    if (HappeningStates.Over(post.State))
                        await rest.ModifyGuildChannelAsync(post.ThreadId, t => t.Archived = false);
                    await rest.ModifyMessageAsync(post.ThreadId, post.ThreadId, m => (m.Embeds, m.Components) = (card.Embeds, card.Components));
                    await rest.ModifyGuildChannelAsync(post.ThreadId, t => (t.Name, t.AppliedTags, t.Archived) = (Clip(card.Title), tags, HappeningStates.Over(state)));
                    post.State = state;
                }
            }
            catch (RestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                // The post was deleted on Discord: the server didn't want it.
                if (post is not null)
                    post.Gone = true;
            }
            catch (RestException ex)
            {
                logger.LogWarning("Showing {Kind} {Id} in the forum {ForumId} of {GuildId} failed: {Message}", kind, id, forumId, guildId, ex.Message);
            }
            await db.SaveChangesAsync();
            return post is { Gone: false } ? post.ThreadId : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    // Closes the kind's posts still open after that long (a Wrapped after its week), tagged done and archived.
    public async Task CloseOlderAsync(ulong guildId, string kind, TimeSpan age)
    {
        await _gate.WaitAsync();
        using var _ = logger.BeginScope(DebugLog.Guild(guildId));
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            var before = time.GetUtcNow() - age;
            foreach (var post in await db.HappeningPosts.Where(p => p.GuildId == guildId && p.Kind == kind && p.State == HappeningStates.Open && !p.Gone && p.CreatedAt < before).ToListAsync())
            {
                try
                {
                    var tags = await TagsAsync(post.ForumId, kind, HappeningStates.Done);
                    await rest.ModifyGuildChannelAsync(post.ThreadId, t => (t.AppliedTags, t.Archived) = (tags, true));
                    post.State = HappeningStates.Done;
                }
                catch (RestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
                {
                    post.Gone = true;
                }
                catch (RestException ex)
                {
                    logger.LogWarning("Closing {Kind} {Id} in the forum of {GuildId} failed: {Message}", kind, post.ThingId, guildId, ex.Message);
                }
            }
            await db.SaveChangesAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    // The forum's tags for the kind and the state, added to it when it lacks them (that needs Manage Channels there).
    private async Task<IEnumerable<ulong>> TagsAsync(ulong forumId, string kind, string state)
    {
        if (await rest.GetChannelAsync(forumId) is not ForumGuildChannel forum)
            return [];
        var wanted = new[] { Tags[kind], Tags[state] };
        var missing = wanted.Where(w => !forum.AvailableTags.Any(t => t.Name == w.Name)).ToList();
        if (missing.Count > 0)
        {
            try
            {
                forum = (ForumGuildChannel)await rest.ModifyGuildChannelAsync(forumId, o => o.AvailableTags =
                [
                    .. forum.AvailableTags.Select(t => new ForumTagProperties(t.Name) { Id = t.Id, Moderated = t.Moderated, EmojiId = t.EmojiId, EmojiName = t.EmojiName }),
                    .. missing.Select(m => new ForumTagProperties(m.Name) { EmojiName = m.Emoji }),
                ]);
            }
            catch (RestException ex)
            {
                logger.LogWarning("Adding the forum {ForumId}'s tags failed: {Message}", forumId, ex.Message);
            }
        }
        return forum.AvailableTags.Where(t => wanted.Any(w => w.Name == t.Name)).Select(t => t.Id).ToList();
    }

    private static string Clip(string title) => title.Length <= TitleLength ? title : title[..(TitleLength - 1)] + "…";
}
