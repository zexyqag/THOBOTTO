using THOBOTTO.Modules;

namespace THOBOTTO.Music.Quiz;

// Stored with SettingsStore under the module id.
public sealed record QuizRules
{
    [Setting("Rounds", Min = 3, Max = 30)]
    public int Rounds { get; init; } = 10;

    [Setting("Time to answer", Help = "How long each clip plays while people answer.", Unit = "seconds", Min = 8, Max = 40)]
    public int ClipSeconds { get; init; } = 20;

    [Setting("Songs from a playlist", Help = "A playlist link (YouTube, Spotify, …) to take the songs from. Empty: the songs played most in this server.")]
    public string? Playlist { get; init; }

    [Setting("Points for the winner", Help = "Given to whoever scores highest (shared on a tie). 0 gives none.", Min = 0, Max = 100000)]
    public int WinnerPoints { get; init; }
}
