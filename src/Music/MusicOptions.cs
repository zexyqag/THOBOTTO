namespace THOBOTTO.Music;

public sealed class LavalinkOptions
{
    public required string BaseAddress { get; set; }

    public required string Passphrase { get; set; }
}

// Helper bot accounts that play music; each can be in one voice channel per guild.
public sealed class MusicOptions
{
    public List<HelperOptions> Helpers { get; set; } = [];
}

public sealed class HelperOptions
{
    public string Token { get; set; } = "";
}
