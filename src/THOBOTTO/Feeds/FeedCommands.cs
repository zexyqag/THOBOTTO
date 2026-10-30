using System.Text;

using NetCord;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;

using THOBOTTO.Access;
using THOBOTTO.Discord;
using THOBOTTO.Modules;

namespace THOBOTTO.Feeds;

[SlashCommand("feeds", "News feeds: patch notes, YouTube channels, blogs, posted as they come", Contexts = [InteractionContextType.Guild])]
public sealed class FeedCommands(FeedWatch watch, ModuleState modules) : GuildCommandModule
{
    [SubSlashCommand("add", "Follow a feed (RSS or Atom), or a page that has one: a Steam game, a YouTube channel, a blog")]
    [RequirePermission(BotPermissions.ManageFeeds)]
    public async Task AddAsync(
        [SlashCommandParameter(Description = "The link: the feed, or the page")] string link,
        [SlashCommandParameter(Description = "Where its entries go; a forum gets a post for each", AllowedChannelTypes = [ChannelType.TextGuildChannel, ChannelType.AnnouncementGuildChannel, ChannelType.ForumGuildChannel])] Channel channel)
    {
        if (!await modules.IsEnabledAsync(GuildId, FeedWatch.ModuleId))
        {
            await RespondAsync(InteractionCallback.Message(Replies.Ephemeral(ModuleRegistry.OffAndHowToTurnOn(FeedWatch.ModuleId))));
            return;
        }
        // Reading the feed (and the page before it) can take longer than Discord waits for an answer.
        await RespondAsync(InteractionCallback.DeferredMessage(MessageFlags.Ephemeral));
        var (feed, problem) = await watch.AddAsync(GuildId, link, channel.Id, Context.User.Id);
        await ModifyResponseAsync(m => m.Content = feed is null
            ? problem
            : $"Following **{feed.Title}** in <#{channel.Id}>; new entries are posted as they come.{(feed.Problem is { } failed ? $"\nIts newest entry couldn't be posted to show how they'll look: {failed}" : feed.PostedAt is not null ? " Its newest is there now, to show how they'll look." : "")}\n-# {feed.Url}");
    }

    [SubSlashCommand("list", "The feeds this server follows")]
    public async Task<InteractionMessageProperties> ListAsync()
    {
        if (!await modules.IsEnabledAsync(GuildId, FeedWatch.ModuleId))
            return Replies.Ephemeral(ModuleRegistry.Off(FeedWatch.ModuleId));
        var feeds = await watch.ListAsync(GuildId);
        if (feeds.Count == 0)
            return Replies.Ephemeral("This server follows no feeds yet: `/feeds add`.");
        var text = new StringBuilder();
        foreach (var feed in feeds)
        {
            text.Append($"**{feed.Title}** → <#{feed.ChannelId}>");
            if (feed.PostedAt is { } posted)
                text.Append($" · last posted <t:{posted.ToUnixTimeSeconds()}:R>");
            if (feed.Problem is { } problem)
                text.Append($"\n⚠️ {problem}");
            text.Append($"\n-# <{feed.Url}>\n");
        }
        return Replies.Ephemeral(text.ToString());
    }

    [SubSlashCommand("remove", "Stop following a feed")]
    [RequirePermission(BotPermissions.ManageFeeds)]
    public async Task<InteractionMessageProperties> RemoveAsync(
        [SlashCommandParameter(Description = "The feed", AutocompleteProviderType = typeof(FeedAutocomplete))] long feed)
        => await watch.RemoveAsync(GuildId, feed, Context.User.Id) is { } removed
            ? Replies.Ephemeral($"No longer following **{removed.Title}**.")
            : Replies.Ephemeral("There's no such feed here. Pick one from the list.");
}

public sealed class FeedAutocomplete(FeedWatch watch) : IAutocompleteProvider<AutocompleteInteractionContext>
{
    public async ValueTask<IEnumerable<ApplicationCommandOptionChoiceProperties>?> GetChoicesAsync(
        ApplicationCommandInteractionDataOption option,
        AutocompleteInteractionContext context)
    {
        var input = option.Value ?? "";
        var channels = context.Guild?.Channels;
        return (await watch.ListAsync(context.Interaction.GuildId!.Value))
            .Where(f => f.Title.Contains(input, StringComparison.OrdinalIgnoreCase) || f.Url.Contains(input, StringComparison.OrdinalIgnoreCase))
            .Take(25)
            .Select(f =>
            {
                var label = $"{f.Title} → #{(channels?.TryGetValue(f.ChannelId, out var c) == true ? c.Name : "a channel")}";
                return new ApplicationCommandOptionChoiceProperties(label.Length <= 100 ? label : label[..100], f.Id);
            });
    }
}
