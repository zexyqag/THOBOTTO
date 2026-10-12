using Microsoft.Extensions.Logging;

using THOBOTTO.Debugging;

namespace THOBOTTO.Tests;

// Which server a kept log line is about: the {GuildId} it was logged with, else the scope it was logged inside.
public class DebugLogTests
{
    private const ulong Server = 123456789012345678;

    private static (DebugLog Log, ILoggerFactory Loggers) On()
    {
        var log = new DebugLog(TimeProvider.System);
        log.Start();
        return (log, LoggerFactory.Create(logging => logging.AddProvider(new DebugLogProvider(log))));
    }

    [Fact]
    public void A_line_logged_with_a_guild_id_carries_its_server()
    {
        var (log, loggers) = On();
        using (loggers)
            loggers.CreateLogger("THOBOTTO.Music.MusicService").LogInformation("Looked up {Identifier} in {GuildId}", "a song", Server);
        var entry = Assert.Single(log.Entries());
        Assert.Equal(Server, entry.GuildId);
        Assert.Equal("Looked up a song in 123456789012345678", entry.Message);
        Assert.Equal("Music", entry.Area);
    }

    [Fact]
    public void A_line_logged_inside_a_servers_scope_carries_it()
    {
        var (log, loggers) = On();
        using (loggers)
        {
            var logger = loggers.CreateLogger("THOBOTTO.Voice.Hearing.VoiceEars");
            using (logger.BeginScope(DebugLog.Guild(Server)))
                logger.LogInformation("Speech to text: {Seconds:0.0} s of speech in {Ms} ms", 1.5, 300);
            logger.LogInformation("A listening tick failed");
        }
        var entries = log.Entries();
        Assert.Equal(2, entries.Count);
        Assert.Null(entries[0].GuildId);
        Assert.Equal(Server, entries[1].GuildId);
    }

    [Fact]
    public void A_line_about_the_bot_carries_no_server()
    {
        var (log, loggers) = On();
        using (loggers)
            loggers.CreateLogger("THOBOTTO.Helpers.HelperFleet").LogInformation("Started {Count} helpers", 2);
        Assert.Null(Assert.Single(log.Entries()).GuildId);
    }

    [Fact]
    public void The_lines_own_guild_id_wins_over_the_scope()
    {
        var (log, loggers) = On();
        using (loggers)
        {
            var logger = loggers.CreateLogger("THOBOTTO.Music.MusicService");
            using (logger.BeginScope(DebugLog.Guild(1)))
                logger.LogWarning("Autoplay in {GuildId} failed", Server);
        }
        Assert.Equal(Server, Assert.Single(log.Entries()).GuildId);
    }

    [Fact]
    public void A_guild_id_is_read_as_a_number_or_as_text()
    {
        var (log, loggers) = On();
        using (loggers)
        {
            var logger = loggers.CreateLogger("THOBOTTO.Discord.DiscordApi");
            foreach (var value in new object[] { Server, (long)Server, (decimal)Server, "123456789012345678" })
                logger.LogWarning("Discord said no for {GuildId}", value);
        }
        Assert.All(log.Entries(), entry => Assert.Equal(Server, entry.GuildId));
    }
}
