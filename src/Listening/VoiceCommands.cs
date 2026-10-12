using Microsoft.EntityFrameworkCore;

using NetCord;
using NetCord.Gateway;
using NetCord.Rest;

using THOBOTTO.Assistant;
using THOBOTTO.Data;
using THOBOTTO.Debugging;
using THOBOTTO.Discord;
using THOBOTTO.Helpers;
using THOBOTTO.Music;
using THOBOTTO.Quotes;
using THOBOTTO.Sounds;
using THOBOTTO.Voice.Speaking;

namespace THOBOTTO.Listening;

// What a voice action does.
public enum VoiceIntent
{
    Play,
    Queue,
    QueueFirst,
    Sound,
    Skip,
    Pause,
    Resume,
    Stop,
    Louder,
    Quieter,
    Volume,
    NowPlaying,
    Shuffle,
    LoopTrack,
    LoopQueue,
    LoopOff,
}

public sealed record VoiceCommand(VoiceIntent Intent, string? Argument);

// Whatever spends points or touches someone else is asked about first.
// Carries out what members say to a helper in their channel ("Jeeves, skip"), as the slash command would, with
// the same permissions; the helper answers in the channel's chat. A sentence is for a helper when it starts with
// one of its names; the language model reads the rest and picks one of the voice actions.
public sealed class VoiceCommands(
    VoiceEars ears,
    HelperFleet fleet,
    MusicService music,
    PersonalityBook personalities,
    Understanding understanding,
    VoiceQuestions questions,
    VoiceTranscript transcript,
    VoiceQuotes quotes,
    HelperSpeech speech,
    VoiceTrace trace,
    SoundBoard sounds,
    IEnumerable<IVoiceActions> features,
    VoicePresence presence,
    GatewayClient gateway,
    RestClient rest,
    IDbContextFactory<BotDbContext> dbFactory,
    TimeProvider time,
    ILogger<VoiceCommands> logger) : IHostedService
{
    private const int VolumeStep = 20;
    // Names of others in the server the language model is told, to set misheard names right; few, as each is
    // more for it to read every time.
    private const int KnownNames = 15;

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
            var heard = new Heard(0, 0, 0, 0, "");
            await understanding.ReadAsync("what's playing", "", Actions(heard, null, null));
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
        // It answers to the listening helper's name, and to the name of the helper playing in that channel.
        var player = music.PlayerIn(heard.GuildId, heard.ChannelId);
        var playing = player is null ? null : player.Mirrors.FirstOrDefault(m => m.VoiceChannelId == heard.ChannelId)?.Helper ?? player.Helper;
        var listener = playing?.UserId == heard.ListenerId ? playing : fleet.Helpers.FirstOrDefault(h => h.UserId == heard.ListenerId);
        // Each name it answers to (its own, and those it also answers to) → its own.
        var meant = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var helper in new[] { playing, listener }.OfType<HelperBot>().Distinct())
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

        string reply;
        speech.ReplyComing(heard.GuildId, heard.ChannelId);
        try
        {
            if (!understanding.On)
                reply = "Voice commands need the language model: the bot's owner sets it on the Integrations page.";
            else
            {
                await ThinkAsync(heard, playing ?? listener!);
                var plan = await UnderstandAsync(heard, player, playing, said);
                if (plan is { Choices.Count: > 0 })
                {
                    trace.Step(heard.Trace, "asked", detail: $"{plan.Reply} ({string.Join(" / ", plan.Choices.Select(c => c.Label))})");
                    await questions.AskAsync(heard, playing ?? listener!, plan);
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
        // Whoever plays here answers (it may have only just started, or just stopped); else the listener.
        var answering = music.PlayerIn(heard.GuildId, heard.ChannelId)?.Helper ?? playing ?? listener!;
        // Nothing to say when the answer was posted already (a draft quote).
        if (reply.Length > 0)
        {
            await personalities.ReplyAsync(heard.GuildId, answering, heard.ChannelId, $"🎙️ <@{heard.UserId}> · {reply}");
            _ = speech.SayAsync(heard.GuildId, heard.ChannelId, reply, SpeechKind.Reply, heard.Trace);
        }
        else
            speech.NoReply(heard.GuildId, heard.ChannelId);
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

    private async Task<string> CarryOutAsync(Heard heard, MusicPlayer? player, HelperBot? helper, VoiceCommand command)
    {
        var guildId = heard.GuildId;
        switch (command.Intent)
        {
            case VoiceIntent.Play or VoiceIntent.Queue or VoiceIntent.QueueFirst:
                var placement = command.Intent switch { VoiceIntent.Queue => Placement.Last, VoiceIntent.QueueFirst => Placement.First, _ => Placement.Now };
                return await music.PlayAsync(guildId, heard.UserId, heard.ChannelId, player?.TextChannelId ?? heard.ChannelId, command.Argument!, placement);
            case VoiceIntent.Sound:
                return await sounds.PlayAsync(guildId, heard.UserId, command.Argument!);
            case var _ when player is null || helper is null:
                return "Nothing is playing here. Say “play” and what.";
            case VoiceIntent.NowPlaying:
                return player.Current is { } current ? $"🎵 {current.Markdown} · {current.Length}" : "Nothing is playing.";
        }

        var user = await MemberAsync(guildId, heard.UserId);
        if (command.Intent == VoiceIntent.Skip)
        {
            if (await music.RefusalAsync(guildId, user, player, ownTrackAllowed: true) is not null)
                return await music.VoteSkipAsync(player, heard.UserId);
            await player.SkipAsync();
            return await LineAsync(guildId, helper, Moments.Skipped);
        }
        if (await music.RefusalAsync(guildId, user, player) is { } refusal)
            return refusal;

        switch (command.Intent)
        {
            case VoiceIntent.Pause:
                await player.SetPausedAsync(true);
                return "⏸️ Paused.";
            case VoiceIntent.Resume:
                await player.SetPausedAsync(false);
                return "▶️ Resumed.";
            case VoiceIntent.Stop:
                await player.StopAsync();
                await music.DisconnectAsync(player);
                return await LineAsync(guildId, helper, Moments.Stopped);
            case VoiceIntent.Louder or VoiceIntent.Quieter or VoiceIntent.Volume:
                if (await music.VolumeRefusalAsync(guildId, user, player) is { } notYours)
                    return notYours;
                var volume = command.Intent switch
                {
                    VoiceIntent.Louder => player.Volume + VolumeStep,
                    VoiceIntent.Quieter => player.Volume - VolumeStep,
                    _ => int.Parse(command.Argument!),
                };
                volume = Math.Clamp(volume, 0, 200);
                await player.SetVolumeAsync(volume);
                return $"🔊 Volume {volume}%.";
            case VoiceIntent.Shuffle:
                await player.ShuffleAsync();
                return "🔀 Shuffled.";
            default:
                player.Loop = command.Intent switch { VoiceIntent.LoopTrack => LoopMode.Track, VoiceIntent.LoopQueue => LoopMode.Queue, _ => LoopMode.Off };
                return player.Loop == LoopMode.Off ? "🔁 Not looping." : $"🔁 Looping the {player.Loop.ToString().ToLowerInvariant()}.";
        }
    }

    // A "hmm" while the language model reads it (spoken only, and not waited for).
    private async Task ThinkAsync(Heard heard, HelperBot helper)
    {
        var line = await personalities.SayAsync(heard.GuildId, helper, Moments.Thinking, new Dictionary<string, string>());
        _ = speech.SayAsync(heard.GuildId, heard.ChannelId, line, SpeechKind.Thinking, heard.Trace);
    }

    // What the language model makes of it: the plan of the action it picked, or null when it's none of them.
    private async Task<VoicePlan?> UnderstandAsync(Heard heard, MusicPlayer? player, HelperBot? playing, string said)
    {
        var actions = Actions(heard, player, playing);
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
        return await action.PlanAsync(new(heard, player, playing, mentioned), args);
    }

    private List<VoiceAction> Actions(Heard heard, MusicPlayer? player, HelperBot? playing)
        => [.. MusicActions(heard, player, playing).Append(QuoteAction(heard, playing)).Append(SoundAction(heard)).Concat(features.SelectMany(f => f.Actions))];

    private VoiceAction SoundAction(Heard heard)
        => new("sound", "Play a sound from the server's soundboard.",
            new() { ["name"] = Schema.Text("The sound's name") }, ["name"],
            async (_, args) => VoicePlan.Done(await sounds.PlayAsync(heard.GuildId, heard.UserId, Schema.String(args, "name") ?? "")),
            ("hit us with the airhorn", """{"name":"airhorn"}"""));

    private VoiceAction QuoteAction(Heard heard, HelperBot? playing)
        => new("quote", "Save as a quote something said in the call just now: someone else's last words (the default), the speaker's own, a person's, or the last few lines.",
            new() { ["who"] = Schema.Text("\"that\", \"me\", a person's name, or \"last N lines\"") }, [],
            async (_, args) => VoicePlan.Done(await quotes.StartAsync(heard, playing ?? fleet.Helpers.First(h => h.UserId == heard.ListenerId), Schema.String(args, "who")) ?? ""),
            ("save what Ana just said as a quote", """{"who":"Ana"}"""));

    // The music commands, for the language model: each comes down to a set phrase's command.
    private IEnumerable<VoiceAction> MusicActions(Heard heard, MusicPlayer? player, HelperBot? playing)
    {
        VoiceAction Simple(string name, string description, VoiceIntent intent)
            => new(name, description, [], [], async (_, _) => VoicePlan.Done(await CarryOutAsync(heard, player, playing, new(intent, null))));
        return
        [
            new("play", "Play or queue a song or playlist. when: now only when they say play or put on (it skips what plays); first for \"next\" or \"after this\"; last for queue or add, words that sound like them (\"q\", \"cue\", \"at\"), or when unsure.",
                new() { ["query"] = Schema.Text("What to search for, or a link"), ["when"] = Schema.OneOf("When it plays", "now", "first", "last") }, ["query", "when"],
                async (_, args) =>
                {
                    var intent = Schema.String(args, "when") switch { "first" => VoiceIntent.QueueFirst, "last" => VoiceIntent.Queue, _ => VoiceIntent.Play };
                    return VoicePlan.Done(await CarryOutAsync(heard, player, playing, new(intent, Schema.String(args, "query") ?? "")));
                }, ("throw on some abba after this one", """{"query":"abba","when":"first"}""")),
            Simple("skip", "Skip the song playing.", VoiceIntent.Skip),
            Simple("pause", "Pause the music.", VoiceIntent.Pause),
            Simple("resume", "Carry on with the music.", VoiceIntent.Resume),
            Simple("stop", "Stop the music and send the helper away.", VoiceIntent.Stop),
            Simple("now_playing", "Say which song is playing.", VoiceIntent.NowPlaying),
            Simple("shuffle", "Shuffle the queue.", VoiceIntent.Shuffle),
            new("volume", "Change the volume: up, down, or to a level (0-200).",
                new() { ["change"] = Schema.OneOf("Which way", "up", "down", "to"), ["level"] = Schema.Number("The level, for \"to\"") }, ["change"],
                async (_, args) =>
                {
                    var (intent, level) = Schema.Int(args, "level") is { } n ? (VoiceIntent.Volume, n.ToString())
                        : Schema.String(args, "change") == "down" ? (VoiceIntent.Quieter, null)
                        : (VoiceIntent.Louder, (string?)null);
                    return VoicePlan.Done(await CarryOutAsync(heard, player, playing, new(intent, level)));
                }),
            new("loop", "Loop the song, the queue, or stop looping.",
                new() { ["what"] = Schema.OneOf("What to loop", "song", "queue", "off") }, ["what"],
                async (_, args) =>
                {
                    var intent = Schema.String(args, "what") switch { "song" => VoiceIntent.LoopTrack, "queue" => VoiceIntent.LoopQueue, _ => VoiceIntent.LoopOff };
                    return VoicePlan.Done(await CarryOutAsync(heard, player, playing, new(intent, null)));
                }),
        ];
    }

    // The helper posts its reply itself, so its line goes without its name in front.
    private Task<string> LineAsync(ulong guildId, HelperBot helper, string moment)
        => personalities.SayAsync(guildId, helper, moment, new Dictionary<string, string>());

    private async Task<GuildUser> MemberAsync(ulong guildId, ulong userId)
        => gateway.Cache.Guilds.TryGetValue(guildId, out var guild) && guild.Users.TryGetValue(userId, out var cached) ? cached : await rest.GetGuildUserAsync(guildId, userId);

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
