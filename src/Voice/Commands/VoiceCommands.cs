using Microsoft.EntityFrameworkCore;

using NetCord;
using NetCord.Gateway;
using NetCord.Rest;

using THOBOTTO.Data;
using THOBOTTO.Debugging;
using THOBOTTO.Discord;
using THOBOTTO.Helpers;
using THOBOTTO.Voice.Hearing;
using THOBOTTO.Voice.Speaking;

namespace THOBOTTO.Voice.Commands;

// Whatever spends points or touches someone else is asked about first.
// Carries out what members say to a helper in their channel ("Jeeves, skip"), as the slash command would, with
// the same permissions; the helper answers in the channel's chat. A sentence is for a helper when it starts with
// one of its names; the language model reads the rest and picks one of the voice actions the features offer.
public sealed class VoiceCommands(
    VoiceEars ears,
    HelperFleet fleet,
    PersonalityBook personalities,
    Understanding understanding,
    VoiceQuestions questions,
    VoiceTranscript transcript,
    HelperSpeech speech,
    VoiceTrace trace,
    IEnumerable<IVoiceActions> features,
    VoicePresence presence,
    GatewayClient gateway,
    IDbContextFactory<BotDbContext> dbFactory,
    TimeProvider time,
    ILogger<VoiceCommands> logger) : IHostedService
{
    // Names of others in the server the language model is told, to set misheard names right; few, as each is
    // more for it to read every time.
    private const int KnownNames = 15;

    private IReadOnlyList<VoiceAction> Actions => [.. features.SelectMany(f => f.Actions)];

    public Task StartAsync(CancellationToken cancellationToken)
    {
        ears.Heard += OnHeardAsync;
        _ = WarmUpAsync();
        return Task.CompletedTask;
    }

    // The language model reads the actions first every time; its server keeps that part once it has read it.
    // A first reading now, so the first command after a start isn't the one waiting for it.
    private async Task WarmUpAsync()
    {
        if (!understanding.On)
            return;
        try
        {
            await understanding.ReadAsync("what's playing", "", Actions);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            logger.LogDebug("Warming up the language model: {Message}", ex.Message);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        ears.Heard -= OnHeardAsync;
        return Task.CompletedTask;
    }

    private async Task OnHeardAsync(Heard heard)
    {
        // It answers to the names of the helpers in that channel: the one it was heard through, and any other there.
        var helpers = HelpersIn(heard);
        // Each name it answers to (its own, and those it also answers to) → its own.
        var meant = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var helper in helpers)
        {
            var answersTo = await personalities.AnswersToAsync(heard.GuildId, helper);
            foreach (var also in answersTo)
                meant.TryAdd(also, answersTo[0]);
        }
        // Anything not said to a helper may be quoted shortly after.
        if (meant.Count == 0 || AddressedTo(heard.Text, meant.Keys) is not var (name, said))
        {
            trace.Step(heard.Trace, "not said to a helper", detail: meant.Count == 0 ? "no helper here" : $"listening for {string.Join(", ", meant.Keys)}");
            transcript.Add(heard);
            return;
        }
        trace.Step(heard.Trace, "said to a helper", detail: $"{meant[name]}: “{said}”");

        var answering = helpers[0];
        string reply;
        speech.ReplyComing(heard.GuildId, heard.ChannelId);
        try
        {
            if (!understanding.On)
                reply = "Voice commands need the language model: the bot's owner sets it on the Integrations page.";
            else
            {
                await ThinkAsync(heard, answering);
                var plan = await UnderstandAsync(heard, answering, said);
                if (plan is { Choices.Count: > 0 })
                {
                    trace.Step(heard.Trace, "asked", detail: $"{plan.Reply} ({string.Join(" / ", plan.Choices.Select(c => c.Label))})");
                    await questions.AskAsync(heard, answering, plan);
                    return;
                }
                reply = plan?.Reply ?? $"I didn't get that: “{said}”.";
            }
            trace.Step(heard.Trace, "did it", detail: reply.Length == 0 ? "(a draft quote)" : reply);
        }
        catch (Exception ex) when (ex is RestException or HttpRequestException or InvalidOperationException)
        {
            logger.LogWarning("A voice command failed: {Message}", ex.Message);
            trace.Step(heard.Trace, "failed", detail: ex.Message);
            reply = "That didn't work just now; try again.";
        }
        // Nothing to say when the answer was posted already (a draft quote).
        if (reply.Length > 0)
        {
            await personalities.ReplyAsync(heard.GuildId, answering, heard.ChannelId, $"🎙️ <@{heard.UserId}> · {reply}");
            _ = speech.SayAsync(heard.GuildId, heard.ChannelId, reply, SpeechKind.Reply, heard.Trace);
        }
        else
            speech.NoReply(heard.GuildId, heard.ChannelId);
    }

    // The helpers in the voice channel it was said in, the one it was heard through first: that one answers, or,
    // when it's gone since, any other still there.
    private List<HelperBot> HelpersIn(Heard heard)
    {
        var present = presence.Snapshot(heard.GuildId);
        return [.. fleet.Helpers
            .Where(h => h.UserId == heard.ListenerId || present.TryGetValue(h.UserId, out var there) && there.ChannelId == heard.ChannelId)
            .OrderByDescending(h => h.UserId == heard.ListenerId)];
    }

    // The name it starts with (one given, as written, in any case) and what's said after it; null when it
    // starts with none of them.
    public static (string Name, string Said)? AddressedTo(string heard, IEnumerable<string> names)
    {
        static string Plain(string word) => new([.. word.ToLowerInvariant().Where(char.IsLetterOrDigit)]);
        var words = heard.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        foreach (var name in names.OrderByDescending(n => n.Length))
        {
            var nameWords = name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(Plain).ToArray();
            if (nameWords.Length > 0 && words.Length > nameWords.Length && nameWords.Select((w, i) => w == Plain(words[i])).All(same => same))
                return (name, string.Join(' ', words.Skip(nameWords.Length)).Trim(' ', ',', '.', '!', '?'));
        }
        return null;
    }

    // A "hmm" while the language model reads it (spoken only, and not waited for).
    private async Task ThinkAsync(Heard heard, HelperBot helper)
    {
        var line = await personalities.SayAsync(heard.GuildId, helper, Moments.Thinking, new Dictionary<string, string>());
        _ = speech.SayAsync(heard.GuildId, heard.ChannelId, line, SpeechKind.Thinking, heard.Trace);
    }

    // What the language model makes of it: the plan of the action it picked, or null when it's none of them.
    private async Task<VoicePlan?> UnderstandAsync(Heard heard, HelperBot helper, string said)
    {
        var actions = Actions;
        var mentioned = questions.Mentioned(heard.GuildId, heard.ChannelId);
        gateway.Cache.Guilds.TryGetValue(heard.GuildId, out var guild);
        string Name(ulong userId) => guild?.Users.TryGetValue(userId, out var u) == true ? u.Nickname ?? u.GlobalName ?? u.Username : "someone";
        var inCall = presence.Snapshot(heard.GuildId).Where(p => p.Value.ChannelId == heard.ChannelId && !p.Value.IsBot).Select(p => p.Key).ToHashSet();
        // Names it may have misheard: those in the call, then others online.
        var others = guild is null ? [] : guild.Users.Values
            .Where(u => !u.IsBot && !inCall.Contains(u.Id) && guild.Presences.TryGetValue(u.Id, out var p) && p.Status != UserStatusType.Offline)
            .Take(KnownNames).Select(u => Name(u.Id));
        var context = $"Speaker: {Name(heard.UserId)}. In the call: {string.Join(", ", inCall.Select(Name))}. Others here: {string.Join(", ", others)}."
            + (mentioned is { } who ? $" Just talked about: {Name(who)}." : "");
        var started = time.GetTimestamp();
        var read = await understanding.ReadAsync(said, context, actions);
        var took = (int)time.GetElapsedTime(started).TotalMilliseconds;
        logger.LogInformation("Language model: {Ms} ms", took);
        trace.Step(heard.Trace, "language model", took, read is var (picked, given) ? $"{picked.Name} {given.GetRawText()}" : "none of the actions");
        if (read is not var (action, args))
            return null;
        await AuditAsync(heard, $"{action.Name} {args.GetRawText()}");
        return await action.PlanAsync(new(heard, helper, mentioned), args);
    }

    // What was done, not what was said.
    private async Task AuditAsync(Heard heard, string details)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        db.AuditEntries.Add(new()
        {
            GuildId = heard.GuildId,
            ActorId = heard.UserId,
            Action = "voice.command",
            Details = details,
            CreatedAt = time.GetUtcNow(),
        });
        await db.SaveChangesAsync();
    }
}
