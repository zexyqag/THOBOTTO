using NetCord;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;

namespace THOBOTTO.Games;

// Several games in one command option, separated by commas, each by any of its names (its name, its label with its
// emoji, its id; in a group DM also with its server). The autocomplete completes the one being typed.
public static class GamePicks
{
    public static IReadOnlyList<string> Split(string text) => [.. text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)];

    // The games named, in order and each once; or what's wrong with a name: none of them, or more than one.
    public static (IReadOnlyList<T> Picked, string? Problem) Parse<T>(string text, IReadOnlyList<T> candidates, Func<T, IEnumerable<string>> names)
    {
        var picked = new List<T>();
        foreach (var name in Split(text))
        {
            var matching = candidates.Where(c => names(c).Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase))).ToList();
            if (matching.Count != 1)
                return ([], matching.Count == 0 ? $"`{name}` isn't one of the games: pick them from the list." : $"`{name}` is a game of more than one server: pick it from the list.");
            if (!picked.Contains(matching[0]))
                picked.Add(matching[0]);
        }
        return picked.Count == 0 ? ([], "Pick a game.") : (picked, null);
    }

    // What's typed so far with the last name completed by each game not picked yet whose label has it.
    public static IEnumerable<ApplicationCommandOptionChoiceProperties> Complete<T>(string text, IReadOnlyList<T> candidates, Func<T, string> label, Func<T, IEnumerable<string>> names)
    {
        var parts = text.Split(',').Select(p => p.Trim()).ToList();
        var typing = parts[^1];
        var before = parts[..^1].Where(p => p.Length > 0).ToList();
        var taken = candidates.Where(c => before.Any(b => names(c).Contains(b, StringComparer.OrdinalIgnoreCase))).ToList();
        return candidates
            .Where(c => !taken.Contains(c) && label(c).Contains(typing, StringComparison.OrdinalIgnoreCase))
            .Select(c => string.Join(", ", before.Append(label(c))))
            .Where(choice => choice.Length <= 100)
            .Take(25)
            .Select(choice => new ApplicationCommandOptionChoiceProperties(choice, choice));
    }

    public static IEnumerable<string> Names(Game game) => [GameDirectory.Label(game), game.Name, game.Id.ToString()];
}

// The server's games, several separated by commas.
public sealed class GamesAutocomplete(GameDirectory games) : IAutocompleteProvider<AutocompleteInteractionContext>
{
    public async ValueTask<IEnumerable<ApplicationCommandOptionChoiceProperties>?> GetChoicesAsync(
        ApplicationCommandInteractionDataOption option,
        AutocompleteInteractionContext context)
        => GamePicks.Complete(option.Value ?? "", await games.ListAsync(context.Interaction.GuildId!.Value), GameDirectory.Label, GamePicks.Names);
}
