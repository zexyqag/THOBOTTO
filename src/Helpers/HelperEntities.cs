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

    public static IReadOnlyList<string> All { get; } = [Joined, Playing, Skipped, Stopped, Finished, Lonely];
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

// Ready-made personalities to start from, and the plain lines of a helper without one.
public sealed record Template(string Name, int Color, IReadOnlyDictionary<string, string[]> Phrases)
{
    public static IReadOnlyList<Template> All { get; } =
    [
        new("DJ Volume", 0xFF3B7F, new Dictionary<string, string[]>
        {
            [Moments.Joined] = ["🎙️ IN THE HOUSE! {helper} live in {channel}!", "🎧 Who called the DJ? Rolling into {channel}!"],
            [Moments.Playing] = ["🔥 COMING UP NEXT: {track}!", "📻 Turn it UP! This one's {track}, thanks to {user}!", "🎶 Banger alert: {track}!"],
            [Moments.Skipped] = ["⏭️ Next! The crowd has spoken!", "⏭️ Scratch that! 💿"],
            [Moments.Stopped] = ["🛑 And that's a wrap, folks!", "🛑 DJ's off duty! Peace! ✌️"],
            [Moments.Finished] = ["📻 Queue's dry! Hit me with more or I'm out! 👋", "That's all the records I brought! Catch you later! 🎉"],
            [Moments.Lonely] = ["🎤 Is this thing on? Nobody's here... DJ out. 😢", "Empty dance floor. I'm packing up! 👋"],
        }),
        new("Jeeves", 0x5B6770, new Dictionary<string, string[]>
        {
            [Moments.Joined] = ["Good evening. I shall be attending to the music in {channel}.", "Very well. I have arrived in {channel}."],
            [Moments.Playing] = ["Now playing {track}, as requested by {user}.", "If I may: {track}. Requested by {user}, naturally.", "{track}. A choice, certainly."],
            [Moments.Skipped] = ["Skipped. I shan't pretend to be sorry.", "As you wish. Moving on."],
            [Moments.Stopped] = ["The music has been stopped. Will that be all?", "Silence, at last. Thank you."],
            [Moments.Finished] = ["The queue is exhausted, as am I. I shall take my leave.", "Nothing further has been requested. Good evening."],
            [Moments.Lonely] = ["It appears I am playing for an empty room. I shall withdraw.", "No one remains. I shall see myself out."],
        }),
    ];

    public static Template Plain { get; } = new("", 0x1DB954, new Dictionary<string, string[]>
    {
        [Moments.Joined] = ["Joined {channel}."],
        [Moments.Playing] = ["🎵 {track}, asked for by {user}."],
        [Moments.Skipped] = ["⏭️ Skipped."],
        [Moments.Stopped] = ["⏹️ Stopped."],
        [Moments.Finished] = ["Nothing left to play, so I'm off. 👋"],
        [Moments.Lonely] = ["Nobody's listening, so I'm off. 👋"],
    });
}
