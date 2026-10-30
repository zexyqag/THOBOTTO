namespace THOBOTTO.Points;

public static class AddPrice
{
    public static double Of(double cost, double perWaiting, int waiting, int playlistLike = 1)
        => cost * playlistLike + perWaiting * waiting;

    public static bool Free(int listeners, int freeWithAtMost) => listeners <= freeWithAtMost;
}
