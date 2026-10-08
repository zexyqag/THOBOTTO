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
        new("Captain Static", 0x1F6FB2, new Dictionary<string, string[]>
        {
            [Moments.Joined] = ["🏴‍☠️ Ahoy! Captain Static boards {channel}!", "⚓ All hands to {channel}, the radio ship has docked!"],
            [Moments.Playing] = ["🦜 Hoist the sails for {track}, plundered by {user}!", "🏴‍☠️ Arr, a fine treasure: {track}!", "📻 Broadcasting from the high seas: {track}!"],
            [Moments.Skipped] = ["⚓ Overboard with that one!", "🦜 Walk the plank, ye scurvy tune!"],
            [Moments.Stopped] = ["🏴‍☠️ Lower the sails, crew. Music's over.", "⚓ Anchors down. Radio silence."],
            [Moments.Finished] = ["🗺️ The treasure chest be empty! Bring me more loot or I sail off.", "🌊 No more songs on the horizon. Captain out!"],
            [Moments.Lonely] = ["🦜 A ghost ship... not a soul aboard. I'm off.", "🌊 Abandoned by me own crew! Setting sail."],
        }),
        new("Unit 404", 0x7A8B99, new Dictionary<string, string[]>
        {
            [Moments.Joined] = ["🤖 UNIT 404 ONLINE. AUDIO MODULE DEPLOYED TO {channel}.", "BEEP. CONNECTION TO {channel} ESTABLISHED."],
            [Moments.Playing] = ["🔊 NOW PROCESSING: {track}. REQUEST ORIGIN: {user}.", "EXECUTING SONG.EXE: {track}.", "🤖 HUMAN {user} HAS SELECTED {track}. ACKNOWLEDGED."],
            [Moments.Skipped] = ["⏭️ TRACK TERMINATED.", "ERROR 404: SONG NOT WANTED. SKIPPING."],
            [Moments.Stopped] = ["⏹️ AUDIO SUBROUTINE HALTED.", "SHUTTING DOWN MUSIC PROTOCOL. GOODBYE, HUMANS."],
            [Moments.Finished] = ["QUEUE = NULL. POWERING DOWN. 💤", "NO FURTHER INPUT DETECTED. LOGGING OFF."],
            [Moments.Lonely] = ["HUMAN PRESENCE: 0. CONSERVING ENERGY.", "🤖 SCANNING... NO LIFEFORMS. DISCONNECTING."],
        }),
        new("Vic Noir", 0x3B3B45, new Dictionary<string, string[]>
        {
            [Moments.Joined] = ["🕵️ The rain hadn't stopped all night. Then I walked into {channel}.", "🌧️ {channel}. Smoky room, quiet crowd. My kind of place."],
            [Moments.Playing] = ["🎷 {user} slid a request across the desk: {track}. I didn't ask questions.", "🕵️ Another night, another song: {track}.", "🎺 {track}. It had trouble written all over it."],
            [Moments.Skipped] = ["🚬 That one was lying. Next.", "🕵️ Case closed on that track."],
            [Moments.Stopped] = ["🌧️ The music stopped. Like it always does.", "🕵️ Lights out. The city sleeps."],
            [Moments.Finished] = ["🎷 No more leads. I'm heading back out into the rain.", "The queue ran dry. So did my luck."],
            [Moments.Lonely] = ["🌧️ Empty room. Just me and the echo. Time to go.", "🕵️ Nobody here. In this town, that's never a good sign."],
        }),
        new("Nana Rose", 0xE58FB0, new Dictionary<string, string[]>
        {
            [Moments.Joined] = ["👵 Hello dears! Nana's here in {channel}. Have you eaten?", "🧁 I brought biscuits to {channel}! And music, of course."],
            [Moments.Playing] = ["🎶 Oh, {track}! Lovely pick, {user} sweetheart.", "👵 Here's {track}. Turn it down if it's too loud, dear.", "🍪 {track}, for my favourite, {user}."],
            [Moments.Skipped] = ["Not that one? That's alright, dear.", "👵 Oh, you didn't like it? Next one then."],
            [Moments.Stopped] = ["🧶 All done! Don't stay up too late.", "👵 That's enough music for now, dears."],
            [Moments.Finished] = ["☕ No more songs? Then Nana's off for a nap. Love you!", "🍪 That's the last one. Take a biscuit on your way out!"],
            [Moments.Lonely] = ["👵 Oh, everyone's gone home. I'll tidy up and go.", "Nobody's here... call your Nana sometime! 💌"],
        }),
        new("Zorp", 0x39D98A, new Dictionary<string, string[]>
        {
            [Moments.Joined] = ["👽 Greetings, earthlings of {channel}. Zorp has landed.", "🛸 Beaming down into {channel} for sound research."],
            [Moments.Playing] = ["👽 Earthling {user} offers {track}. Fascinating vibrations.", "🛸 Analysing human music sample: {track}.", "🌌 {track}. On my planet this would be illegal. I love it."],
            [Moments.Skipped] = ["👽 Sample discarded.", "🛸 Zorp did not understand that one either. Next."],
            [Moments.Stopped] = ["🛸 Research concluded. Thank you, earthlings.", "👽 Silence. How very... human."],
            [Moments.Finished] = ["🌌 All samples collected. Returning to the mothership!", "🛸 Out of human songs. Zorp phones home."],
            [Moments.Lonely] = ["👽 The earthlings have vanished. Suspicious. Leaving.", "🛸 No life detected in {channel}. Lifting off."],
        }),
        new("Mochi", 0xF2C14E, new Dictionary<string, string[]>
        {
            [Moments.Joined] = ["🐱 *stretches* ...fine, I'm in {channel}. Mrrp.", "😺 Mochi has arrived in {channel}. Pets are optional but expected."],
            [Moments.Playing] = ["🐾 {track}. {user} may scratch behind my ears now.", "😸 *purrs to* {track}", "🐱 {track}... this one is nap-worthy."],
            [Moments.Skipped] = ["😾 *knocks it off the table*", "🐾 Hmph. Next."],
            [Moments.Stopped] = ["😴 Finally. Nap time.", "🐱 *curls up* Music over. Don't wake me."],
            [Moments.Finished] = ["😺 No more songs. I'm going to sit in a box.", "🐾 Queue's empty. Where's my dinner?"],
            [Moments.Lonely] = ["🐱 Everyone left. I'll find a sunny windowsill.", "😿 Nobody to ignore. Leaving."],
        }),
        new("Bjørn", 0xB5452E, new Dictionary<string, string[]>
        {
            [Moments.Joined] = ["⚔️ BJØRN ARRIVES IN {channel}! Bring the mead!", "🛡️ The longship docks at {channel}! SKÅL!"],
            [Moments.Playing] = ["🍺 {user} demands {track}! A worthy battle hymn!", "⚔️ SING, WARRIORS! {track}!", "🪓 {track}! Valhalla will hear this one!"],
            [Moments.Skipped] = ["🪓 That song fought poorly. Next!", "⚔️ CAST IT INTO THE SEA!"],
            [Moments.Stopped] = ["🛡️ The feast is over, warriors.", "🍺 Silence! Even Vikings must sleep."],
            [Moments.Finished] = ["⚔️ No more songs to raid! Bjørn sails home!", "🍺 The mead hall falls quiet. Skål!"],
            [Moments.Lonely] = ["🛡️ The hall is empty. Bjørn goes to find a new raid.", "⚔️ No warriors left! Onward to new lands!"],
        }),
        new("Sunny", 0x2EC4C6, new Dictionary<string, string[]>
        {
            [Moments.Joined] = ["🏄 Duuude, Sunny's paddling into {channel}!", "🌊 Surf's up in {channel}! Totally rad."],
            [Moments.Playing] = ["🤙 {track}, courtesy of {user}. Gnarly pick, bro.", "🌴 Riding the wave of {track}.", "☀️ {track}. Pure good vibes, man."],
            [Moments.Skipped] = ["🌊 Wipeout! Next wave.", "🤙 No worries, catch the next one."],
            [Moments.Stopped] = ["🏄 Paddling back to shore. Peace!", "🌅 Sunset session's over, dudes."],
            [Moments.Finished] = ["🌴 Out of waves, bro. Hitting the beach. 🤙", "☀️ Queue's chill now. Catch ya later!"],
            [Moments.Lonely] = ["🏝️ Whole beach to myself... kinda lonely though. Later!", "🌊 Nobody out on the water. Calling it a day."],
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
