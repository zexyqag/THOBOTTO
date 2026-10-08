namespace THOBOTTO.Music;

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

// A helper's character: how it's called, its colour, and what it says. Per helper account, so the
// same everywhere. Moments without phrases of their own fall back to the built-in character.
public sealed class HelperProfile
{
    public ulong UserId { get; init; }

    public string? Nickname { get; set; }

    public int? Color { get; set; }

    // Moment → phrases; {track}, {user}, {channel} and {helper} are filled in.
    public Dictionary<string, List<string>> Phrases { get; set; } = [];
}

public sealed record Character(string Nickname, int Color, IReadOnlyDictionary<string, string[]> Phrases)
{
    // The built-in characters, handed out by helper order; any further helpers get the plain one.
    public static IReadOnlyList<Character> BuiltIn { get; } =
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

    public static Character Plain { get; } = new("", 0x1DB954, new Dictionary<string, string[]>
    {
        [Moments.Joined] = ["Joined {channel}."],
        [Moments.Playing] = ["🎵 {track}, asked for by {user}."],
        [Moments.Skipped] = ["⏭️ Skipped."],
        [Moments.Stopped] = ["⏹️ Stopped."],
        [Moments.Finished] = ["Nothing left to play, so I'm off. 👋"],
        [Moments.Lonely] = ["Nobody's listening, so I'm off. 👋"],
    });

    public static Character For(int helperIndex) => helperIndex < BuiltIn.Count ? BuiltIn[helperIndex] : Plain;
}
