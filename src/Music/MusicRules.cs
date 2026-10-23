using THOBOTTO.Modules;

namespace THOBOTTO.Music;

public static class HelperPriorities
{
    public const string MusicFirst = "music";
    public const string ListeningFirst = "listening";
    public const string FirstCome = "first";
}

// Stored with SettingsStore under the module id.
public sealed record MusicRules
{
    [Setting("Helpers' volume", Help = "The same for every helper everywhere in the server, at once. Members set their own on top in Discord (right-click a helper → User Volume).", Unit = "%", Min = 0, Max = 100)]
    public int HelpersVolume { get; init; } = 50;

    [Setting("Leave when nothing plays after", Help = "A helper with nothing playing for this long goes.", Unit = "minutes", Min = 1, Max = 120)]
    public int IdleMinutes { get; init; } = 3;

    [Setting("With nothing playing, stay while someone is in the channel", Help = "The helper waits there for the next request as long as anyone is with it, and goes once nobody is left.")]
    public bool StayWithCompany { get; init; }

    [Setting("Leave when nobody is left after", Help = "Playing or not. 0 goes as soon as the last member leaves.", Unit = "minutes", Min = 0, Max = 120)]
    public int LonelyMinutes { get; init; } = 3;

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

    [Setting("Others add songs last", Help = "When only DJs control the music, members who may not skip put songs at the end of the queue; otherwise their /play and /queue first put them first (without skipping).")]
    public bool OthersQueueLast { get; init; }

    [Setting("When helpers run out", Choices = [HelperPriorities.MusicFirst + "=Music first: a listening helper starts playing", HelperPriorities.ListeningFirst + "=Listening first: a playing helper stops to listen", HelperPriorities.FirstCome + "=First come, first served: nothing is taken over"])]
    public string Priority { get; init; } = HelperPriorities.MusicFirst;

    [Setting("Lower the music while people talk", Help = "Where a helper can hear the channel (playing through the voice relay, or listening for voice commands): the music gets quieter while anyone talks, and comes back after.")]
    public bool DuckWhileTalking { get; init; }

    [Setting("Lowered to", Unit = "% of the volume", Min = 0, Max = 100)]
    public int DuckPercent { get; init; } = 40;

    [Setting("Members may have a helper join them", Help = "With this on, members can have a free helper join their voice channel whenever they're in one, ready to play. It leaves again when nothing plays for the idle time.")]
    public bool AutoJoinAllowed { get; init; }

    [Setting("Votes to skip", Help = "When only DJs control the music, others skip by vote: this share of the listeners. 0: no vote, anyone listening skips.", Unit = "% of listeners", Min = 0, Max = 100)]
    public int SkipVotePercent { get; init; } = 50;
}
