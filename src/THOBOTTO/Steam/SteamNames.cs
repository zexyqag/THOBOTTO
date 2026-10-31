using System.Text.RegularExpressions;

namespace THOBOTTO.Steam;

// Game names as people write them and as Steam does: "Counter-Strike 2" and "Counter Strike 2", "DOOM" and "Doom",
// "Satisfactory™" and "Satisfactory" are the same game.
public static partial class SteamNames
{
    public static IReadOnlyList<string> Words(string name) => [.. Word().Matches(name.ToLowerInvariant()).Select(m => m.Value)];

    public static bool Same(string a, string b) => string.Concat(Words(a)) == string.Concat(Words(b));

    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex Word();
}
