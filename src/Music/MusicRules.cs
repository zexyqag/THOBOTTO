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

    [Setting("Autoplay", Help = "When the queue runs out, keep playing songs like the last one (from YouTube Music's mixes). Each player can switch it with /music autoplay.")]
    public bool Autoplay { get; init; }

    [Setting("Autoplay adds", Help = "How many similar songs autoplay queues at a time.", Unit = "songs", Min = 1, Max = 10)]
    public int AutoplayBatch { get; init; } = 3;

    [Setting("Only DJs control the music", Help = "Skipping, stopping and the like need music.dj, except for your own tracks. Others can vote to skip.")]
    public bool DjOnly { get; init; }

    [Setting("Members may have a helper join them", Help = "With this on, members can have a free helper join their voice channel whenever they're in one, ready to play. It leaves again when nothing plays for the idle time.")]
    public bool AutoJoinAllowed { get; init; }

    [Setting("Votes to skip", Help = "When only DJs control the music, others skip by vote: this share of the listeners. 0: no vote, anyone listening skips.", Unit = "% of listeners", Min = 0, Max = 100)]
    public int SkipVotePercent { get; init; } = 50;
}
