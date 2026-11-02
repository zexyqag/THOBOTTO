using NetCord.Gateway;

using THOBOTTO.Happenings;
using THOBOTTO.Modules;

namespace THOBOTTO.Events;

public sealed class EventPostings(SettingsStore settings, HappeningForum forum) : IPostingSource
{
    public async Task<IReadOnlyList<Posting>> PostingsAsync(Guild guild)
    {
        var rules = await settings.GetAsync<EventRules>(guild.Id, EventBoard.ModuleId);
        var setAt = Posting.Settings(guild.Id, EventBoard.ModuleId);
        List<Posting> postings =
        [
            new(EventBoard.ModuleId, "Events", new Landing.WhereStarted("where each is planned"), null),
            new(EventBoard.ModuleId, "Voice channels of events and sessions", rules.VoiceCategoryId is { } category ? new Landing.In(category) : new Landing.WhereStarted("in the category of each one's card"), setAt),
        ];
        if (rules.ImportDiscordEvents && await forum.ShowsAsync(guild.Id, EventBoard.EventHappening.Id) && await forum.ForumOfAsync(guild.Id) is { } forumId)
            postings.Add(new(EventBoard.ModuleId, "Events made in Discord's own calendar", new Landing.In(forumId), setAt));
        return postings;
    }
}
