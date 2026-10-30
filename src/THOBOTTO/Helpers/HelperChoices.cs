using NetCord;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;

namespace THOBOTTO.Helpers;

// What the /setup helpers commands offer to pick: a moment, what to do with phrases, and the helpers, templates
// and personalities by name.
public enum Moment
{
    Joined,
    Playing,
    Skipped,
    Stopped,
    Finished,
    Lonely,
    Wrapped,
}

public enum PhraseAction
{
    List,
    Add,
    Remove,
    Clear,
}

// The helpers in the server by the names they go by there; the value is the helper's id.
public sealed class HelperAutocomplete(HelperFleet fleet, PersonalityBook book) : IAutocompleteProvider<AutocompleteInteractionContext>
{
    public async ValueTask<IEnumerable<ApplicationCommandOptionChoiceProperties>?> GetChoicesAsync(
        ApplicationCommandInteractionDataOption option,
        AutocompleteInteractionContext context)
    {
        var guildId = context.Interaction.GuildId!.Value;
        var input = option.Value ?? "";
        var choices = new List<ApplicationCommandOptionChoiceProperties>();
        foreach (var helper in fleet.Helpers.Where(h => h.InGuild(guildId)))
        {
            var label = $"{await book.NameAsync(guildId, helper)} ({helper.Name})";
            if (label.Contains(input, StringComparison.OrdinalIgnoreCase))
                choices.Add(new(label, helper.UserId.ToString()));
        }
        return choices.Take(25);
    }

    // The helper a choice stands for; null for anything but a choice.
    public static ulong? Id(string? choice) => ulong.TryParse(choice, out var id) ? id : null;
}

public sealed class TemplateAutocomplete : IAutocompleteProvider<AutocompleteInteractionContext>
{
    public ValueTask<IEnumerable<ApplicationCommandOptionChoiceProperties>?> GetChoicesAsync(
        ApplicationCommandInteractionDataOption option,
        AutocompleteInteractionContext context)
    {
        var input = option.Value ?? "";
        return new(PersonalityFile.Templates
            .Where(t => t.Name.Contains(input, StringComparison.OrdinalIgnoreCase))
            .Take(25)
            .Select(t => new ApplicationCommandOptionChoiceProperties(t.Name, t.Name)));
    }
}

public sealed class PersonalityAutocomplete(PersonalityBook book) : IAutocompleteProvider<AutocompleteInteractionContext>
{
    public async ValueTask<IEnumerable<ApplicationCommandOptionChoiceProperties>?> GetChoicesAsync(
        ApplicationCommandInteractionDataOption option,
        AutocompleteInteractionContext context)
    {
        var input = option.Value ?? "";
        return (await book.ListAsync(context.Interaction.GuildId!.Value))
            .Where(p => p.Name.Contains(input, StringComparison.OrdinalIgnoreCase))
            .Take(25)
            .Select(p => new ApplicationCommandOptionChoiceProperties(p.Name, p.Id));
    }
}
