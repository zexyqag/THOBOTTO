using NetCord.Gateway;

namespace THOBOTTO.Modules;

// Where something the bot posts lands in a server now.
public abstract record Landing
{
    private Landing()
    {
    }

    // A channel (or, for voice channels, a category) of the server.
    public sealed record In(ulong ChannelId) : Landing;

    // Wherever each one is started: a command's channel, a voice channel's chat.
    public sealed record WhereStarted(string Where) : Landing;

    // Not posted; Why says what would change that.
    public sealed record Nowhere(string Why) : Landing;
}

// Something a module posts, where it lands now, and the panel page where that's chosen.
public sealed record Posting(string Module, string What, Landing Landing, string? SetAt)
{
    public static string Settings(ulong guildId, string module) => $"/g/{guildId}/settings/{module}";
}

// What a module posts in a server's channels, resolved from its settings: for the panel's Where things go page, which
// shows it by module and by channel. Every channel a module's settings choose shows up in one of these.
public interface IPostingSource
{
    Task<IReadOnlyList<Posting>> PostingsAsync(Guild guild);
}
