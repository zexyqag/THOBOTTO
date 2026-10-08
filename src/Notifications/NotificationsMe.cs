using NetCord.Rest;
using NetCord.Services.ApplicationCommands;

using THOBOTTO.Notifications;

namespace THOBOTTO;

public sealed partial class MeCommands
{
    [SubSlashCommand("notifications", "What the bot DMs you about; give a topic to turn it on or off")]
    public async Task<InteractionMessageProperties> NotificationsAsync(
        [SlashCommandParameter(Description = "Topic", AutocompleteProviderType = typeof(TopicAutocomplete))] string? topic = null,
        [SlashCommandParameter(Description = "DM me about it (leave out to flip it)")] bool? dm = null)
    {
        var notifier = Get<Notifier>();
        var guildId = Context.Guild!.Id;
        var topics = await notifier.TopicsAsync(guildId);
        var mine = await notifier.SubscriptionsAsync(guildId, Context.User.Id);
        if (topic is null)
            return Replies.Ephemeral("DMs (change with `/me notifications topic:…`):\n" + string.Join('\n', topics.Select(t => $"{(mine.Contains(t.Id) ? "🔔" : "🔕")} `{t.Id}`: {t.Description}")));

        if (topics.FirstOrDefault(t => t.Id == topic) is not { } known)
            return Replies.Ephemeral("There's no such topic. Pick one from the list.");
        var on = dm ?? !mine.Contains(topic);
        await notifier.SetAsync(guildId, Context.User.Id, topic, on);
        return Replies.Ephemeral(on ? $"🔔 You'll get a DM when: {known.Description.ToLowerInvariant()}." : $"🔕 No more DMs for `{topic}`.");
    }
}
