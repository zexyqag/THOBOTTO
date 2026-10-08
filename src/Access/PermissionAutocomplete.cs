using NetCord;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;

namespace THOBOTTO.Access;

public sealed class PermissionAutocomplete : IAutocompleteProvider<AutocompleteInteractionContext>
{
    public ValueTask<IEnumerable<ApplicationCommandOptionChoiceProperties>?> GetChoicesAsync(
        ApplicationCommandInteractionDataOption option,
        AutocompleteInteractionContext context)
    {
        var input = option.Value ?? "";
        var choices = BotPermissions.All
            .Where(p => p.Id.Contains(input, StringComparison.OrdinalIgnoreCase) || p.Description.Contains(input, StringComparison.OrdinalIgnoreCase))
            .Take(25)
            .Select(p => (p.Id, Label: $"{p.Id}: {p.Description}"))
            .Select(p => new ApplicationCommandOptionChoiceProperties(p.Label.Length <= 100 ? p.Label : p.Label[..100], p.Id));

        return new(choices);
    }
}
