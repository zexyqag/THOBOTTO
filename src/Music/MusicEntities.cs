namespace THOBOTTO.Music;

// A player as it was when last saved, so it can come back after the bot restarts.
public sealed class SavedMusicPlayer
{
    public ulong GuildId { get; init; }

    public ulong VoiceChannelId { get; init; }

    // A PlayerState as JSON.
    public required string State { get; init; }

    public DateTimeOffset SavedAt { get; init; }
}

public sealed record MirrorState(ulong HelperId, ulong VoiceChannelId);

public sealed record PlayerState(
    ulong HelperId,
    ulong VoiceChannelId,
    ulong TextChannelId,
    IReadOnlyList<MirrorState> Mirrors,
    Track? Current,
    long Position,
    bool Paused,
    LoopMode Loop,
    int Volume,
    IReadOnlyList<Track> Queue,
    ulong? NowPlayingMessageId,
    bool NowPlayingByHelper,
    bool Autoplay = false,
    IReadOnlyList<Track>? Later = null);

// A queue saved under a name, for anyone in the server to play again.
public sealed class SavedPlaylist
{
    public long Id { get; init; }

    public ulong GuildId { get; init; }

    public required string Name { get; set; }

    // The name lowercased: one playlist per name, whatever the case.
    public required string Key { get; init; }

    public ulong CreatorId { get; set; }

    // A list of Track as JSON.
    public required string Tracks { get; set; }

    public int TrackCount { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
