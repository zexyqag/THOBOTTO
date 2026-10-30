using THOBOTTO.Helpers;

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
    IReadOnlyList<MirrorState> Mirrors,
    Track? Current,
    long Position,
    bool Paused,
    LoopMode Loop,
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

// A song a helper played, for Wrapped and listening stats.
public sealed class PlayRecord
{
    public long Id { get; init; }

    public ulong GuildId { get; init; }

    public ulong VoiceChannelId { get; init; }

    public ulong HelperId { get; init; }

    // The personality the helper wore then, if any.
    public string? Personality { get; init; }

    public required string Artist { get; init; }

    public required string Title { get; init; }

    public required string Source { get; init; }

    public string? Uri { get; init; }

    public long LengthMs { get; init; }

    public long PlayedMs { get; init; }

    // Ended well before its end: skipped or stopped.
    public bool Skipped { get; init; }

    // Queued by autoplay rather than a member.
    public bool Autoplay { get; init; }

    public ulong RequestedBy { get; init; }

    public DateTimeOffset StartedAt { get; init; }
}

// Someone who heard a played song (there at its start or its end, not deafened).
public sealed class PlayListener
{
    public long PlayId { get; init; }

    public ulong UserId { get; init; }
}
