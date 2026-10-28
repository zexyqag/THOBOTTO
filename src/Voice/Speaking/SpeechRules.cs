using THOBOTTO.Modules;

namespace THOBOTTO.Voice.Speaking;

// What helpers say out loud (always written too), in their personality's voice. Stored with SettingsStore under
// the module id.
public sealed record SpeechRules
{
    [Setting("Replies to voice commands", Help = "\"Skipped.\", \"You have 16 points\": short answers to what members said.")]
    public bool Replies { get; init; } = true;

    [Setting("Questions", Help = "\"Give Ana 5 points?\", so it can be answered without looking.")]
    public bool Questions { get; init; } = true;

    [Setting("Now playing", Help = "A line in the personality's style when a song starts, like a radio DJ.")]
    public bool NowPlaying { get; init; }

    [Setting("Hello and goodbye", Help = "The personality's line when it joins or leaves a call.")]
    public bool JoinAndLeave { get; init; }

    [Setting("Speech loudness", Help = "Helpers' speech is evened out to about as loud as music, then set to this much of that. The music goes quieter while they speak.", Unit = "%", Min = 50, Max = 200)]
    public int Loudness { get; init; } = 100;

    [Setting("Members' lines", Help = "/helper say and /helper ask: a helper says what a member typed, in their name (\"Ana says: …\").")]
    public bool Lines { get; init; } = true;

    [Setting("A line costs", Help = "Nothing while the points module is off.", Unit = "points", Min = 0, Max = 1000)]
    public double LineCost { get; init; } = 2;

    [Setting("Between a member's lines", Unit = "seconds", Min = 0, Max = 3600)]
    public int LineCooldownSeconds { get; init; } = 30;

    [Setting("Thinking sounds", Help = "A short \"hmm\" or \"one moment\" in the personality's style while a request takes a moment (when the language model reads it).")]
    public bool Thinking { get; init; } = true;
}
