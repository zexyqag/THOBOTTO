using NetCord.Gateway;

using THOBOTTO.Modules;

namespace THOBOTTO.Happenings;

public sealed class HappeningPostings(SettingsStore settings, IEnumerable<HappeningKind> kinds) : IPostingSource
{
    public async Task<IReadOnlyList<Posting>> PostingsAsync(Guild guild)
    {
        var rules = await settings.GetAsync<HappeningRules>(guild.Id, HappeningForum.ModuleId);
        var shown = kinds.Where(k => rules.Shows(k.Id)).Select(k => k.Label.ToLowerInvariant()).ToList();
        var setAt = Posting.Settings(guild.Id, HappeningForum.ModuleId);
        return rules.ForumChannelId is { } forum
            ? [new(HappeningForum.ModuleId, shown.Count == 0 ? "Nothing: every kind is switched off" : $"A post each, besides where it's started: {string.Join(", ", shown)}", new Landing.In(forum), setAt)]
            : [new(HappeningForum.ModuleId, "Happenings (events, sessions, bets, proposals, results, Wrapped)", new Landing.Nowhere("only where each is started, until a forum is set"), setAt)];
    }
}
