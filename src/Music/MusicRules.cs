using THOBOTTO.Modules;

namespace THOBOTTO.Music;

// Stored with SettingsStore under the module id.
public sealed record MusicRules
{
    [Setting("Leave when idle after", Help = "Helpers leave after this long with nobody listening, or nothing playing.", Unit = "minutes", Min = 1, Max = 120)]
    public int IdleMinutes { get; init; } = 3;

    [Setting("Longest queue", Unit = "tracks", Min = 1, Max = 5000)]
    public int MaxQueue { get; init; } = 200;

    // Spotify finds the song there and plays it from YouTube.
    [Setting("Search plain words on", Choices = ["ytsearch=YouTube", "ytmsearch=YouTube Music", "scsearch=SoundCloud", "spsearch=Spotify"])]
    public string DefaultSearch { get; init; } = "ytsearch";

    public static string SearchName(string prefix) => prefix switch
    {
        "ytmsearch" => "YouTube Music",
        "scsearch" => "SoundCloud",
        "spsearch" => "Spotify",
        _ => "YouTube",
    };

    [Setting("Only DJs control the music", Help = "Skipping, stopping and the like need music.dj, except for your own tracks.")]
    public bool DjOnly { get; init; }
}
