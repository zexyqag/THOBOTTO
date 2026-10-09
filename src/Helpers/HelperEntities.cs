namespace THOBOTTO.Helpers;

// When a helper speaks; each module that uses helpers adds its own.
public static class Moments
{
    public const string Joined = "joined";
    public const string Playing = "playing";
    public const string Skipped = "skipped";
    public const string Stopped = "stopped";
    public const string Finished = "finished";
    public const string Lonely = "lonely";
    public const string Wrapped = "wrapped";

    public static IReadOnlyList<string> All { get; } = [Joined, Playing, Skipped, Stopped, Finished, Lonely, Wrapped];
}

// A helper bot added in the panel (others come from configuration). The token is encrypted with
// the data protection keys.
public sealed class HelperAccount
{
    public ulong UserId { get; init; }

    public required string ProtectedToken { get; set; }

    public ulong AddedById { get; init; }

    public DateTimeOffset AddedAt { get; init; }
}

// A character a server can give any of its helpers: the name it goes by there, its look and colour,
// and what it says.
public sealed class Personality
{
    public long Id { get; init; }

    public ulong GuildId { get; init; }

    public required string Name { get; set; }

    public int? Color { get; set; }

    // Shown as the helper's avatar in this server only.
    public byte[]? Avatar { get; set; }

    public string? AvatarType { get; set; }

    // Moment → phrases; {track}, {user}, {channel} and {helper} are filled in.
    public Dictionary<string, List<string>> Phrases { get; set; } = [];

    public DateTimeOffset CreatedAt { get; init; }
}

// Which personality a helper wears in a server.
public sealed class HelperAssignment
{
    public ulong GuildId { get; init; }

    public ulong HelperId { get; init; }

    public long PersonalityId { get; set; }
}
