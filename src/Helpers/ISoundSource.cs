namespace THOBOTTO.Helpers;

// Sounds a helper can drop into a line with [sound name]: the server's library by name, else Discord's own default
// soundboard sounds, which every server has.
public interface ISoundSource
{
    Task<bool> HasAsync(ulong guildId, string name);

    // The sound as audio (mono, at its own rate), or null when neither place has it.
    Task<Spoken?> SoundAsync(ulong guildId, string name, CancellationToken ct);
}
