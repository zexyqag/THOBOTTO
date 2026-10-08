using THOBOTTO.Modules;

namespace THOBOTTO.Music;

// Stored with SettingsStore under the module id.
public sealed record MusicRules
{
    [Setting("Leave when idle after", Help = "Helpers leave after this long with nobody listening, or nothing playing.", Unit = "minutes", Min = 1, Max = 120)]
    public int IdleMinutes { get; init; } = 3;

    [Setting("Longest queue", Unit = "tracks", Min = 1, Max = 5000)]
    public int MaxQueue { get; init; } = 200;

    [Setting("Search plain words on", Choices = ["ytsearch=YouTube", "scsearch=SoundCloud"])]
    public string DefaultSearch { get; init; } = "ytsearch";

    [Setting("Only DJs control the music", Help = "Skipping, stopping and the like need music.dj, except for your own tracks.")]
    public bool DjOnly { get; init; }
}
