namespace THOBOTTO.Modules;

// What a module's settings add up to now, read with other modules' (where things end up posted): shown at the top
// of its settings page and in /setup show. A module may speak on another's page where it can't be named from there.
public interface ISettingsSummary
{
    // The module whose settings page it's shown on.
    string ModuleId { get; }

    // Channels are named by the caller: #name on the panel, a mention in Discord.
    Task<string?> NowAsync(ulong guildId, Func<ulong, string> channel);
}

public static class SettingsSummaries
{
    public static async Task<string?> NowAsync(this IEnumerable<ISettingsSummary> summaries, string moduleId, ulong guildId, Func<ulong, string> channel)
    {
        List<string> lines = [];
        foreach (var summary in summaries.Where(s => s.ModuleId == moduleId))
        {
            if (await summary.NowAsync(guildId, channel) is { Length: > 0 } line)
                lines.Add(line);
        }
        return lines.Count == 0 ? null : string.Join(' ', lines);
    }
}
