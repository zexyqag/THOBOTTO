using System.Reflection;

using THOBOTTO.Games;
using THOBOTTO.Modules;

namespace THOBOTTO.Tests;

// The panel's Where things go page knows every channel a module's settings choose: each one is read by a posting source.
public class PostingTests
{
    private static readonly SettingKind[] Channels = [SettingKind.TextChannel, SettingKind.ForumChannel, SettingKind.VoiceChannel, SettingKind.Category];

    // Settings the sources read through the same helper the module itself uses.
    private static readonly Dictionary<string, string> ReadThrough = new()
    {
        ["AppealsChannelId"] = "AppealsTo(",
        ["GreetingChannelId"] = "GreetingChannel(",
        ["SessionsChannelId"] = "HomeAsync(",
    };

    [Fact]
    public void Every_channel_setting_shows_where_things_go()
    {
        var sources = string.Join('\n', Directory.EnumerateFiles(Source(), "*Postings.cs", SearchOption.AllDirectories).Select(File.ReadAllText));
        var settings = typeof(GameDirectory).Assembly.GetTypes()
            .SelectMany(t => t.GetProperties())
            .Where(p => p.GetCustomAttribute<SettingAttribute>() is { } setting && Channels.Contains(setting.Kind))
            // Channels left out of something (the archive's, the hall of fame's) aren't destinations.
            .Where(p => !p.Name.StartsWith("Excluded", StringComparison.Ordinal))
            .ToList();

        Assert.NotEmpty(settings);
        var missing = settings
            .Where(p => !sources.Contains($".{p.Name}", StringComparison.Ordinal) && !(ReadThrough.TryGetValue(p.Name, out var helper) && sources.Contains(helper, StringComparison.Ordinal)))
            .Select(p => $"{p.DeclaringType!.Name}.{p.Name}")
            .ToList();
        Assert.True(missing.Count == 0, "Channel settings no posting source reads:\n" + string.Join('\n', missing));
    }

    private static string Source()
    {
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        while (folder is not null && !File.Exists(Path.Combine(folder.FullName, "THOBOTTO.slnx")))
            folder = folder.Parent;
        Assert.NotNull(folder);
        return Path.Combine(folder!.FullName, "src", "THOBOTTO");
    }
}
