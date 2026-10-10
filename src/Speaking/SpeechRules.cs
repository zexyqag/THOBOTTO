using THOBOTTO.Modules;

namespace THOBOTTO.Speaking;

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

    [Setting("Thinking sounds", Help = "A short \"hmm\" or \"one moment\" in the personality's style while a request takes a moment (when the language model reads it).")]
    public bool Thinking { get; init; } = true;
}
