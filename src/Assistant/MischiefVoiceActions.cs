using System.Text.Json;

using NetCord;
using NetCord.Gateway;
using NetCord.Rest;

using THOBOTTO.Mischief;

namespace THOBOTTO.Assistant;

// Mischief by voice ("rename Ana to Captain Chaos", "paint Bob pink for two hours"): it costs points and
// touches others, so each is asked about first.
public sealed class MischiefVoiceActions(MischiefActions mischief, PeopleFinder people, GatewayClient gateway, RestClient rest) : IVoiceActions
{
    // When no time is said; the question shows it.
    private const int DefaultHours = 1;

    private static readonly string[] Me = ["me", "myself", "my", "i"];

    public IEnumerable<VoiceAction> Actions =>
    [
        new("rename", "Change someone's nickname (or your own).",
            new() { ["person"] = Schema.Text("Whose nickname changes, as said"), ["name"] = Schema.Text("The new nickname") }, ["person", "name"],
            (r, a) => ForPersonAsync(r, a, "Rename", p => $"Rename {p.Name} to **{Schema.String(a, "name")}**?",
                (actor, user) => mischief.RenameAsync(actor, user, Schema.String(a, "name"), null)),
            ("change my nickname to Captain Chaos", """{"person":"me","name":"Captain Chaos"}""")),
        new("colour", "Give someone's name a colour for some hours.",
            new() { ["person"] = Schema.Text("Who, as said"), ["colour"] = Schema.Text("The colour"), ["hours"] = Schema.Number("For how many hours, if said") }, ["person", "colour"],
            (r, a) => ForPersonAsync(r, a, "Colour", p => $"Colour {p.Name} {Schema.String(a, "colour")} for {Hours(a)} h?",
                (actor, user) => mischief.PaintAsync(actor, user, Schema.String(a, "colour") ?? "", Hours(a))),
            ("paint bob pink for two hours", """{"person":"bob","colour":"pink","hours":2}""")),
        new("uncolour", "Wash the colour off someone's name (or your own).",
            new() { ["person"] = Schema.Text("Who, as said") }, ["person"],
            (r, a) => ForPersonAsync(r, a, "Wash the colour off", p => $"Wash the colour off {p.Name}?",
                (actor, user) => mischief.UnpaintAsync(actor, user.Id))),
        new("lock_name", "Lock someone's current nickname for some hours, so nobody can change it.",
            new() { ["person"] = Schema.Text("Who, as said"), ["hours"] = Schema.Number("For how many hours, if said") }, ["person"],
            (r, a) => ForPersonAsync(r, a, "Lock the name of", p => $"Lock {p.Name}'s name for {Hours(a)} h?",
                (actor, user) => mischief.LockAsync(actor, user, Hours(a)))),
        new("unlock_name", "Break the lock on someone's nickname (or your own).",
            new() { ["person"] = Schema.Text("Who, as said") }, ["person"],
            (r, a) => ForPersonAsync(r, a, "Unlock the name of", p => $"Break the lock on {p.Name}'s name?",
                (actor, user) => mischief.UnlockAsync(actor, user.Id))),
        new("shield", "Shield yourself from renames and colours for some hours.",
            new() { ["hours"] = Schema.Number("For how many hours, if said") }, [],
            (r, a) => Task.FromResult(VoicePlan.Ask($"Shield yourself for {Hours(a)} h?",
                [new("Yes, shield me", async () => (await mischief.ShieldAsync(Actor(r), Hours(a))).Text)]))),
        new("buy_name_back", "Buy your own name back (resets your nickname).",
            [], [],
            (r, _) => Task.FromResult(VoicePlan.Ask("Buy your name back?",
                [new("Yes, buy it back", async () => (await mischief.BuyBackAsync(Actor(r), await MemberAsync(r.Heard.GuildId, r.Heard.UserId))).Text)]))),
    ];

    private static int Hours(JsonElement args) => Math.Max(1, Schema.Int(args, "hours") ?? DefaultHours);

    // Who it's about (the speaker for "me"): asked to confirm, or which one when it isn't clear.
    private async Task<VoicePlan> ForPersonAsync(VoiceRequest request, JsonElement args, string verb, Func<Person, string> question, Func<MischiefActor, GuildUser, Task<MischiefResult>> act)
    {
        var heard = request.Heard;
        if (Schema.String(args, "person") is not { } spoken)
            return VoicePlan.Done($"{verb} whom?");
        var found = Me.Contains(spoken.Trim().ToLowerInvariant())
            ? [new Person(heard.UserId, "yourself", Whereabouts.InTheCall, false, 1)]
            : await people.FindAsync(heard.GuildId, heard.ChannelId, spoken, request.Mentioned);
        if (found.Count == 0)
            return VoicePlan.Done($"I don't know who “{spoken}” is.");
        VoiceChoice Choice(Person person, string label)
            => new(label, async () => (await act(Actor(request), await MemberAsync(heard.GuildId, person.Id))).Text, person.Id);
        return found is [var only]
            ? VoicePlan.Ask(question(only), [Choice(only, "Yes, do it")])
            : VoicePlan.Ask($"{verb} which {spoken}?", [.. found.Select(p => Choice(p, p.Label))]);
    }

    private MischiefActor Actor(VoiceRequest request)
    {
        var heard = request.Heard;
        var name = gateway.Cache.Guilds.TryGetValue(heard.GuildId, out var guild) && guild.Users.TryGetValue(heard.UserId, out var user) ? user.Username : "Someone";
        return new(heard.GuildId, heard.UserId, name, heard.ChannelId);
    }

    private async Task<GuildUser> MemberAsync(ulong guildId, ulong userId)
        => gateway.Cache.Guilds.TryGetValue(guildId, out var guild) && guild.Users.TryGetValue(userId, out var cached) ? cached : await rest.GetGuildUserAsync(guildId, userId);
}
