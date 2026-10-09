using THOBOTTO.Modules;

namespace THOBOTTO.Voice;

// Stored with SettingsStore under the module id.
public sealed record VoiceRules
{
    [Setting("Most new channels at once", Help = "When this many hub channels exist, people joining a hub stay in it until one empties. 0 is no limit.", Unit = "channels", Min = 0, Max = 100)]
    public int MaxChannels { get; init; }
}
