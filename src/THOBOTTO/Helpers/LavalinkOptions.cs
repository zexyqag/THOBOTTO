namespace THOBOTTO.Helpers;

public sealed class LavalinkOptions
{
    public required string BaseAddress { get; set; }

    public required string Passphrase { get; set; }

    // A folder the bot keeps Lavalink's plugin jars in, for Lavalink to mount as its plugins folder; empty leaves plugins to Lavalink.
    public string? PluginsDirectory { get; set; }

    // The plugins kept there, by name: where each jar is published, and its sha256. One with no address is left out.
    public Dictionary<string, PluginSource> Plugins { get; set; } = new();
}

public sealed class PluginSource
{
    public string? Url { get; set; }

    public string? Sha256 { get; set; }
}
