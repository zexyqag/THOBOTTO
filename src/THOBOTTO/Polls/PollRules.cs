using THOBOTTO.Modules;

namespace THOBOTTO.Polls;

// Stored with SettingsStore under the module id.
public sealed record PollRules
{
    [Setting("Activity polls in the voice channel's chat", Help = "A poll asked in a voice channel's Activity is posted in that channel's chat as it opens, votable there by those in the call, and becomes its result when it closes. Off, only the result is posted.")]
    public bool InVoiceChat { get; init; } = true;

    [Setting("Take in Discord's own polls", Help = "When a poll made with the + beside the message box ends, its result goes to the happenings forum like the bot's polls. One way only: the bot can't vote for anyone, so its polls don't become Discord's.")]
    public bool TakeInDiscordPolls { get; init; }
}
