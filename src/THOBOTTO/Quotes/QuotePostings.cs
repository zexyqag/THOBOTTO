using NetCord.Gateway;

using THOBOTTO.Fame;
using THOBOTTO.Modules;

namespace THOBOTTO.Quotes;

public sealed class QuotePostings(SettingsStore settings) : IPostingSource
{
    public async Task<IReadOnlyList<Posting>> PostingsAsync(Guild guild)
    {
        var rules = await settings.GetAsync<QuoteRules>(guild.Id, QuoteCommands.ModuleId);
        var showcase = (await settings.GetAsync<FameRules>(guild.Id, HallOfFame.ModuleId)).ShowcaseChannelId;
        Landing landing = rules.PostsTo(showcase) is { } channel ? new Landing.In(channel)
            : new Landing.Nowhere(rules.PostQuotes ? "saved only, until quotes or the hall of fame have a channel" : "saved only: posting them is off");
        return [new(QuoteCommands.ModuleId, "Saved quotes", landing, Posting.Settings(guild.Id, QuoteCommands.ModuleId))];
    }
}
