using NetCord;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;

namespace THOBOTTO.Notifications;

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
