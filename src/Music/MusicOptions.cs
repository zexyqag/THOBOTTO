namespace THOBOTTO.Music;

public sealed class LavalinkOptions
{
    public required string BaseAddress { get; set; }

    public required string Passphrase { get; set; }

    // Signs the YouTube plugin in (a throwaway Google account's). Handed over once Lavalink is up rather than
    // in Lavalink's own settings, where a refused token stops Lavalink from starting at all.
    public string? YoutubeRefreshToken { get; set; }
}
