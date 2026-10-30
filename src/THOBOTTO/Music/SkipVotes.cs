namespace THOBOTTO.Music;

public static class SkipVotes
{
    // That share of those listening or watching, and at least one.
    public static int Needed(int present, int percent) => Math.Max(1, (int)Math.Ceiling(present * percent / 100.0));
}
