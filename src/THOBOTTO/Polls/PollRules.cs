using THOBOTTO.Modules;

namespace THOBOTTO.Polls;

// Stored with SettingsStore under the module id.
public sealed record PollRules
{
    [Setting("Activity polls in the voice channel's chat", Help = "A poll asked in a voice channel's Activity is posted in that channel's chat as it opens, votable there by those in the call, and becomes its result when it closes. Off, only the result is posted.")]
    public bool InVoiceChat { get; init; } = true;
}
