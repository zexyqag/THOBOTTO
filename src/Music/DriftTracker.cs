namespace THOBOTTO.Music;

// Keeps helpers playing along in step with the leader. Times are Lavalink's clock (unix ms).
// The leader's position is kept as when its track would have started at normal speed (report
// time minus position): the median of its last few reports, as one can be off by most of a
// second; until it reports, a guess from when the track was sent or resumed.
public sealed class DriftTracker
{
    // Further apart than this (ms), a mirror seeks to where the leader is.
    public const long Tolerance = 300;

    private readonly List<long> _leaderStarts = [];
    private readonly Dictionary<ulong, int> _strikes = [];
    private long _guessedStart;
    private long _pausedAt;
    private bool _paused;

    // Reports from before the track started or resumed are about the old state.
    private long _playingSince;

    public void Start(long now)
    {
        _playingSince = _guessedStart = now;
        _paused = false;
        _leaderStarts.Clear();
        _strikes.Clear();
    }

    public void Pause(long now)
    {
        if (_paused)
            return;
        _pausedAt = Position(now);
        _paused = true;
        _leaderStarts.Clear();
    }

    public void Resume(long now)
    {
        if (!_paused)
            return;
        _guessedStart = now - _pausedAt;
        _playingSince = now;
        _paused = false;
        _leaderStarts.Clear();
    }

    // Where the leader is now (ms into the track).
    public long Position(long now) => _paused ? _pausedAt : now - LeaderStart;

    public void Leader(long at, long position)
    {
        if (_paused || at < _playingSince)
            return;
        _leaderStarts.Add(at - position);
        if (_leaderStarts.Count > 3)
            _leaderStarts.RemoveAt(0);
    }

    // A mirror's report. Returns how far off (ms) it is when it should seek to the leader: out of
    // step on two reports in a row, as one alone may be a glitch.
    public long? Mirror(ulong mirrorId, long at, long position)
    {
        if (_paused || at < _playingSince || _leaderStarts.Count == 0)
            return null;

        var drift = LeaderStart - (at - position);
        if (Math.Abs(drift) <= Tolerance)
        {
            _strikes.Remove(mirrorId);
            return null;
        }
        if ((_strikes[mirrorId] = _strikes.GetValueOrDefault(mirrorId) + 1) < 2)
            return null;
        _strikes.Remove(mirrorId);
        return drift;
    }

    private long LeaderStart => _leaderStarts.Count == 0 ? _guessedStart : _leaderStarts.Order().ElementAt(_leaderStarts.Count / 2);
}
