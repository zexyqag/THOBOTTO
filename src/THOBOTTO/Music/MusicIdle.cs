using THOBOTTO.Helpers;

namespace THOBOTTO.Music;

public static class MusicIdle
{
    // Whether a player should go, and with which goodbye: nothing has played for the idle time (unless company
    // keeps it, where the server wants that), or nobody has been with it for the lonely time, playing or not.
    public static string? LeaveReason(MusicRules rules, bool playing, TimeSpan idleFor, int listeners, TimeSpan? lonelyFor)
    {
        if (!playing && idleFor >= TimeSpan.FromMinutes(rules.IdleMinutes) && !(rules.StayWithCompany && listeners > 0))
            return Moments.Finished;
        if (listeners == 0 && lonelyFor is { } lonely && lonely >= TimeSpan.FromMinutes(rules.LonelyMinutes))
            return Moments.Lonely;
        return null;
    }
}
