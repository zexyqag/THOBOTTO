using NetCord;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;

namespace THOBOTTO.Modules;

public sealed class ModuleAutocomplete : IAutocompleteProvider<AutocompleteInteractionContext>
{
    public ValueTask<IEnumerable<ApplicationCommandOptionChoiceProperties>?> GetChoicesAsync(
        ApplicationCommandInteractionDataOption option,
        AutocompleteInteractionContext context)
    {
        var input = option.Value ?? "";
        var choices = ModuleRegistry.All
            .Where(m => m.Id.Contains(input, StringComparison.OrdinalIgnoreCase))
            .Take(25)
            .Select(m => new ApplicationCommandOptionChoiceProperties(m.Id, m.Id));

        return new(choices);
    }
}
