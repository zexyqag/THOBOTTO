namespace THOBOTTO.Lastfm;

// A member's Last.fm account, for every server they listen in. The session key is encrypted with
// the data protection keys.
public sealed class LastfmLink
{
    public ulong UserId { get; init; }

    public required string Username { get; set; }

    public required string ProtectedSessionKey { get; set; }

    // Paused by the member; the link stays.
    public bool Scrobbling { get; set; } = true;

    public DateTimeOffset LinkedAt { get; set; }
}
