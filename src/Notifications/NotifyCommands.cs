using NetCord;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;

namespace THOBOTTO.Notifications;

[SlashCommand("notify", "Choose what the bot DMs you about", Contexts = [InteractionContextType.Guild])]
public sealed class NotifyCommands(Notifier notifier) : ApplicationCommandModule<ApplicationCommandContext>
{
    [SubSlashCommand("list", "What you can be DMed about, and what you get")]
    public async Task<InteractionMessageProperties> ListAsync()
    {
        var guildId = Context.Guild!.Id;
        var topics = await notifier.TopicsAsync(guildId);
        var mine = await notifier.SubscriptionsAsync(guildId, Context.User.Id);
        return Replies.Ephemeral("DMs (change with `/notify set`):\n" + string.Join('\n', topics.Select(t => $"{(mine.Contains(t.Id) ? "🔔" : "🔕")} `{t.Id}`: {t.Description}")));
    }

    [SubSlashCommand("set", "Turn DMs for a topic on or off")]
    public async Task<InteractionMessageProperties> SetAsync(
        [SlashCommandParameter(Description = "Topic", AutocompleteProviderType = typeof(TopicAutocomplete))] string topic,
        [SlashCommandParameter(Description = "DM me about it")] bool dm)
    {
        var guildId = Context.Guild!.Id;
        if ((await notifier.TopicsAsync(guildId)).FirstOrDefault(t => t.Id == topic) is not { } known)
            return Replies.Ephemeral("There's no such topic. Pick one from the list.");

        await notifier.SetAsync(guildId, Context.User.Id, topic, dm);
        return Replies.Ephemeral(dm ? $"🔔 You'll get a DM when: {known.Description.ToLowerInvariant()}." : $"🔕 No more DMs for `{topic}`.");
    }
}

public sealed class TopicAutocomplete(Notifier notifier) : IAutocompleteProvider<AutocompleteInteractionContext>
{
    public async ValueTask<IEnumerable<ApplicationCommandOptionChoiceProperties>?> GetChoicesAsync(
        ApplicationCommandInteractionDataOption option,
        AutocompleteInteractionContext context)
    {
        var input = option.Value ?? "";
        var topics = await notifier.TopicsAsync(context.Interaction.GuildId!.Value);
        return topics
            .Where(t => t.Id.Contains(input, StringComparison.OrdinalIgnoreCase) || t.Description.Contains(input, StringComparison.OrdinalIgnoreCase))
            .Take(25)
            .Select(t => (t.Id, Label: $"{t.Id}: {t.Description}"))
            .Select(t => new ApplicationCommandOptionChoiceProperties(t.Label.Length <= 100 ? t.Label : t.Label[..100], t.Id));
    }
}
