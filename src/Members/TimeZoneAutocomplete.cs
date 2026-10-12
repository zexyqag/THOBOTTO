using NetCord;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;

namespace THOBOTTO.Members;

public sealed class TimeZoneAutocomplete : IAutocompleteProvider<AutocompleteInteractionContext>
{
    public ValueTask<IEnumerable<ApplicationCommandOptionChoiceProperties>?> GetChoicesAsync(
        ApplicationCommandInteractionDataOption option,
        AutocompleteInteractionContext context)
    {
        var input = (option.Value ?? "").Replace(' ', '_');
        var choices = TimeZones.Ids
            .Where(id => id.Contains('/') && id.Contains(input, StringComparison.OrdinalIgnoreCase))
            .OrderBy(id => !id.Split('/')[^1].StartsWith(input, StringComparison.OrdinalIgnoreCase))
            .ThenBy(id => id)
            .Take(25)
            .Select(id => new ApplicationCommandOptionChoiceProperties(id, id));
        return new(choices);
    }
}
