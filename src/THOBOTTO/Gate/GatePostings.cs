using NetCord.Gateway;

using THOBOTTO.Moderation;
using THOBOTTO.Modules;

namespace THOBOTTO.Gate;

public sealed class GatePostings(SettingsStore settings) : IPostingSource
{
    public async Task<IReadOnlyList<Posting>> PostingsAsync(Guild guild)
    {
        var rules = await settings.GetAsync<GateRules>(guild.Id, Gatekeeper.ModuleId);
        var log = (await settings.GetAsync<ModRules>(guild.Id, CaseBook.ModuleId)).LogChannelId;
        var setAt = Posting.Settings(guild.Id, Gatekeeper.ModuleId);
        List<Posting> postings = [];
        if (rules.Verify != VerifyModes.Off)
            postings.Add(new(Gatekeeper.ModuleId, "The rules and Verify button", rules.WaitingChannelId is { } waiting ? new Landing.In(waiting) : new Landing.Nowhere("newcomers can't verify until there's a waiting channel (/setup gate prepare makes one)"), setAt));
        postings.Add(new(Gatekeeper.ModuleId, "Raid alerts and newcomers to let in", (rules.ReviewChannelId ?? log) is { } alerts ? new Landing.In(alerts) : new Landing.Nowhere("not posted until alerts or the moderation log have a channel"), setAt));
        if (rules.Greet)
            postings.Add(new(Gatekeeper.ModuleId, "Greetings", Gatekeeper.GreetingChannel(rules, guild) is { } greeting ? new Landing.In(greeting) : new Landing.Nowhere("no greeting channel, and Discord's system messages channel is off"), setAt));
        return postings;
    }
}
