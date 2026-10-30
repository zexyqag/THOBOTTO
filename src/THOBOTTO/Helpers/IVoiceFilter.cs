namespace THOBOTTO.Helpers;

// Runs a personality's filter chain (VoiceFilters) on what an engine said, for checking one before it's kept.
public interface IVoiceFilter
{
    // What ffmpeg says is wrong with the chain, tried on a moment of sound; null when it runs.
    Task<string?> CheckAsync(string chain, CancellationToken ct = default);
}
