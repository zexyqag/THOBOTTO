using THOBOTTO.Points;

namespace THOBOTTO.Assistant;

// "Give Ana five points for carrying": kudos by voice, confirmed first (it spends the speaker's points).
public sealed class KudosVoiceActions(PointsEngine points, PeopleFinder people) : IVoiceActions
{
    // When no amount is said; the question shows it, so it's easy to call off.
    private const int DefaultAmount = 10;

    public IEnumerable<VoiceAction> Actions =>
    [
        new("kudos", "Give someone some of your points as thanks.",
            new() { ["person"] = Schema.Text("Who, as said"), ["amount"] = Schema.Number("How many points, if said"), ["reason"] = Schema.Text("What for, if said") },
            ["person"], KudosAsync, ("give ana five points for carrying us", """{"person":"ana","amount":5,"reason":"carrying us"}""")),
    ];

    private async Task<VoicePlan> KudosAsync(VoiceRequest request, System.Text.Json.JsonElement args)
    {
        var heard = request.Heard;
        if (Schema.String(args, "person") is not { } spoken)
            return VoicePlan.Done("Kudos for whom?");
        var found = await people.FindAsync(heard.GuildId, heard.ChannelId, spoken, request.Mentioned);
        if (found.Count == 0)
            return VoicePlan.Done($"I don't know who “{spoken}” is.");
        var amount = Math.Max(1, Schema.Int(args, "amount") ?? DefaultAmount);
        var reason = Schema.String(args, "reason");
        var what = $"{points.Rules(heard.GuildId).Format(amount)}{(reason is null ? "" : $" for {reason}")}";
        VoiceChoice Give(Person person, string label) => new(label, async () => (await points.GiveAsync(heard.GuildId, heard.UserId, person.Id, person.IsBot, amount, reason)).Text, person.Id);
        return found is [var only]
            ? VoicePlan.Ask($"Give {only.Name} {what}?", [Give(only, "Yes, give it")])
            : VoicePlan.Ask($"Give {what} to which {spoken}?", [.. found.Select(p => Give(p, p.Label))]);
    }
}
