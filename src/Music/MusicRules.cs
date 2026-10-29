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

    [Setting("Cost to add", Help = "Points for each song or playlist a member adds beyond their free ones for the day; nothing while the points module is off.", Unit = "points", Min = 0, Max = 1000)]
    public double AddCost { get; init; } = 1;

    [Setting("Free adds a day", Help = "Each member's first adds of the day cost nothing.", Min = 0, Max = 1000)]
    public int FreeAddsPerDay { get; init; } = 50;

    [Setting("More for each of your songs waiting", Help = "Added to the cost for each song the member already has in the queue, so no one fills it alone.", Unit = "points", Min = 0, Max = 1000)]
    public double CostPerWaiting { get; init; } = 1;

    [Setting("A playlist costs like", Help = "A playlist is priced as this many songs.", Unit = "songs", Min = 1, Max = 100)]
    public int PlaylistCostsLike { get; init; } = 3;

    [Setting("Free with this many listening or fewer", Help = "Adding costs nothing while at most this many people are in the channel; 0: never free this way.", Min = 0, Max = 99)]
    public int FreeWithListeners { get; init; } = 2;

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

    [Setting("Votes on queued songs", Help = "Anyone in the channel votes for queued songs; reordering puts the most voted first once they have enough votes.", Choices = [QueueVoting.Count + "=Counted and shown", QueueVoting.Reorder + "=Reorder the queue by them"])]
    public string SongVotes { get; init; } = QueueVoting.Reorder;

    [Setting("Votes to move a song", Help = "With reordering: a song needs this many votes before it moves ahead.", Min = 1, Max = 100)]
    public int VotesToMove { get; init; } = 2;

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
