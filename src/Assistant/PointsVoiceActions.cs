using THOBOTTO.Points;

namespace THOBOTTO.Assistant;

// "How many points do I have", "who's winning": the points, said in the channel (nothing changes, so nothing
// is asked).
public sealed class PointsVoiceActions(PointsEngine points, PeopleFinder people) : IVoiceActions
{
    private const int Leaders = 5;

    public IEnumerable<VoiceAction> Actions =>
    [
        new("points", "Say how many points the speaker (or someone else) has.",
            new() { ["person"] = Schema.Text("Whose, if someone else's") }, [],
            async (r, a) =>
            {
                var heard = r.Heard;
                var rules = points.Rules(heard.GuildId);
                if (Schema.String(a, "person") is { } spoken && spoken.ToLowerInvariant() is not ("me" or "my" or "myself"))
                {
                    if ((await people.FindAsync(heard.GuildId, heard.ChannelId, spoken, r.Mentioned)).FirstOrDefault() is not { } person)
                        return VoicePlan.Done($"I don't know who “{spoken}” is.");
                    var theirs = await points.GetAsync(heard.GuildId, person.Id);
                    return VoicePlan.Done($"<@{person.Id}> has {rules.Format(theirs?.Balance ?? 0)}.");
                }
                var mine = await points.GetAsync(heard.GuildId, heard.UserId);
                return VoicePlan.Done($"You have {rules.Format(mine?.Balance ?? 0)}.");
            },
            ("how many points does ana have", """{"person":"ana"}""")),
        new("points_top", "Say who has the most points.",
            [], [],
            async (r, _) =>
            {
                var rules = points.Rules(r.Heard.GuildId);
                var top = await points.TopAsync(r.Heard.GuildId, Leaders);
                return VoicePlan.Done(top.Count == 0 ? "Nobody has points yet."
                    : "🏆 " + string.Join(" · ", top.Select((s, i) => $"{i + 1}. <@{s.UserId}> {rules.Format(s.Balance)}")));
            }),
    ];
}
