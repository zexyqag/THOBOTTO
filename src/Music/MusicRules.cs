namespace THOBOTTO.Music;

// Stored with SettingsStore under the module id.
public sealed record MusicRules
{
    // Helpers leave after this long with nobody listening, or nothing playing.
    public int IdleMinutes { get; init; } = 3;

    public int MaxQueue { get; init; } = 200;

    // Where plain words are searched: "ytsearch" (YouTube) or "scsearch" (SoundCloud).
    public string DefaultSearch { get; init; } = "ytsearch";

    // When on, skipping, stopping and the like need music.dj, except for your own tracks.
    public bool DjOnly { get; init; }
}
