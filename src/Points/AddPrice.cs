namespace THOBOTTO.Points;

// What adding to a shared queue costs once the member's free adds for the day are used: the price (a playlist like
// several songs) and more for each of their own already waiting. With few enough listening, adding is free.
public static class AddPrice
{
    public static double Of(double cost, double perWaiting, int waiting, int playlistLike = 1)
        => cost * playlistLike + perWaiting * waiting;

    public static bool Free(int listeners, int freeWithAtMost) => listeners <= freeWithAtMost;
}
