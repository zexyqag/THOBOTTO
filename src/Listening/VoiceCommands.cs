using Microsoft.EntityFrameworkCore;

using NetCord;
using NetCord.Gateway;
using NetCord.Rest;

using THOBOTTO.Assistant;
using THOBOTTO.Data;
using THOBOTTO.Helpers;
using THOBOTTO.Music;
using THOBOTTO.Quotes;
using THOBOTTO.Speaking;
using THOBOTTO.Voice;

namespace THOBOTTO.Listening;

// Carries out what members say to the helper playing in their channel ("Jeeves, skip"), as the slash
// command would, with the same permissions; the helper answers in the channel's chat. Set phrases go at
// once; anything else goes to the language model (when there is one), which picks one of the voice actions.
// Whatever spends points or touches someone else is asked about first.
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
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        ears.Heard -= OnHeardAsync;
        return Task.CompletedTask;
    }

    private async Task OnHeardAsync(Heard heard)
    {
        // Answering a question just asked, or changing a draft quote, needs no name.
        if (await questions.AnswerAsync(heard) is not null || await quotes.EditAsync(heard))
            return;

        // It answers to the listening helper's name, and to the name of the helper playing in that channel.
        var player = music.PlayerIn(heard.GuildId, heard.ChannelId);
        var playing = player is null ? null : player.Mirrors.FirstOrDefault(m => m.VoiceChannelId == heard.ChannelId)?.Helper ?? player.Helper;
        var listener = playing?.UserId == heard.ListenerId ? playing : fleet.Helpers.FirstOrDefault(h => h.UserId == heard.ListenerId);
        var names = new List<string>();
        foreach (var helper in new[] { playing, listener }.OfType<HelperBot>().Distinct())
            names.Add(await personalities.NameAsync(heard.GuildId, helper));
        // Anything not said to a helper may be quoted shortly after.
        if (names.Count == 0 || VoiceCommandParser.Parse(heard.Text, names) is not { } command)
        {
            transcript.Add(heard);
            return;
        }

        string reply;
        speech.ReplyComing(heard.GuildId, heard.ChannelId);
        try
        {
            var plan = default(VoicePlan);
            if (command.Intent == VoiceIntent.Unknown && understanding.On)
            {
                await ThinkAsync(heard, playing ?? listener!);
                plan = await UnderstandAsync(heard, player, playing, command);
            }
            if (plan is { Choices.Count: > 0 })
            {
                await questions.AskAsync(heard, playing ?? listener!, plan);
                return;
            }
            reply = command.Intent == VoiceIntent.Quote ? await quotes.StartAsync(heard, playing ?? listener!, command.Argument) ?? ""
                : plan?.Reply ?? await CarryOutAsync(heard, player, playing, command);
        }
        catch (Exception ex) when (ex is RestException or HttpRequestException or InvalidOperationException)
        {
            logger.LogWarning("A voice command failed: {Message}", ex.Message);
            reply = "That didn't work just now; try again.";
        }
        // Whoever plays here answers (it may have only just started, or just stopped); else the listener.
        var answering = music.PlayerIn(heard.GuildId, heard.ChannelId)?.Helper ?? playing ?? listener!;
        // Nothing to say when the answer was posted already (a draft quote).
        if (reply.Length > 0)
        {
            await music.ReplyAsync(heard.GuildId, answering, heard.ChannelId, $"🎙️ <@{heard.UserId}> · {reply}");
            _ = speech.SayAsync(heard.GuildId, heard.ChannelId, reply, SpeechKind.Reply);
        }
        else
            speech.NoReply(heard.GuildId, heard.ChannelId);
        if (command.Intent != VoiceIntent.Unknown)
            await AuditAsync(heard, command.Argument is { } argument ? $"{command.Intent} {argument}" : command.Intent.ToString());
    }

    private async Task<string> CarryOutAsync(Heard heard, MusicPlayer? player, HelperBot? helper, VoiceCommand command)
    {
        var guildId = heard.GuildId;
        switch (command.Intent)
        {
            case VoiceIntent.Play or VoiceIntent.Queue or VoiceIntent.QueueFirst:
                var placement = command.Intent switch { VoiceIntent.Queue => Placement.Last, VoiceIntent.QueueFirst => Placement.First, _ => Placement.Now };
                return await music.PlayAsync(guildId, heard.UserId, heard.ChannelId, player?.TextChannelId ?? heard.ChannelId, command.Argument!, placement);
            case VoiceIntent.Unknown:
                return $"I didn't get that: “{command.Said}”. Try “play …”, “queue …”, “skip”, “pause”, “louder” or “what's playing”.";
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
        _ = speech.SayAsync(heard.GuildId, heard.ChannelId, line, SpeechKind.Thinking);
    }

    // What the language model makes of it: the plan of the action it picked, or null when it's none of them.
    private async Task<VoicePlan?> UnderstandAsync(Heard heard, MusicPlayer? player, HelperBot? playing, VoiceCommand command)
    {
        var actions = MusicActions(heard, player, playing).Append(QuoteAction(heard, playing)).Concat(features.SelectMany(f => f.Actions)).ToList();
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
        var read = await understanding.ReadAsync(command.Said, context, actions);
        logger.LogInformation("Language model: {Ms} ms", (int)time.GetElapsedTime(started).TotalMilliseconds);
        if (read is not var (action, args))
            return null;
        await AuditAsync(heard, $"{action.Name} {args.GetRawText()}");
        return await action.PlanAsync(new(heard, player, playing, mentioned), args);
    }

    private VoiceAction QuoteAction(Heard heard, HelperBot? playing)
        => new("quote", "Save as a quote something said in the call just now: someone else's last words (the default), the speaker's own, a person's, or the last few lines.",
            new() { ["who"] = Schema.Text("\"that\", \"me\", a person's name, or \"last N lines\"") }, [],
            async (_, args) => VoicePlan.Done(await quotes.StartAsync(heard, playing ?? fleet.Helpers.First(h => h.UserId == heard.ListenerId), Schema.String(args, "who")) ?? ""),
            ("save what Ana just said as a quote", """{"who":"Ana"}"""));

    // The music commands, for the language model: each comes down to a set phrase's command.
    private IEnumerable<VoiceAction> MusicActions(Heard heard, MusicPlayer? player, HelperBot? playing)
    {
        VoiceAction Simple(string name, string description, VoiceIntent intent)
            => new(name, description, [], [], async (_, _) => VoicePlan.Done(await CarryOutAsync(heard, player, playing, new(name, intent, null, name))));
        return
        [
            new("play", "Play a song or playlist. when: now (the default; skips what plays), first (next in line, for \"after this\"), last (end of the queue, for \"queue\" or \"add\").",
                new() { ["query"] = Schema.Text("What to search for, or a link"), ["when"] = Schema.OneOf("When it plays", "now", "first", "last") }, ["query", "when"],
                async (_, args) =>
                {
                    var intent = Schema.String(args, "when") switch { "first" => VoiceIntent.QueueFirst, "last" => VoiceIntent.Queue, _ => VoiceIntent.Play };
                    return VoicePlan.Done(await CarryOutAsync(heard, player, playing, new("play", intent, Schema.String(args, "query") ?? "", "play")));
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
                    return VoicePlan.Done(await CarryOutAsync(heard, player, playing, new("volume", intent, level, "volume")));
                }),
            new("loop", "Loop the song, the queue, or stop looping.",
                new() { ["what"] = Schema.OneOf("What to loop", "song", "queue", "off") }, ["what"],
                async (_, args) =>
                {
                    var intent = Schema.String(args, "what") switch { "song" => VoiceIntent.LoopTrack, "queue" => VoiceIntent.LoopQueue, _ => VoiceIntent.LoopOff };
                    return VoicePlan.Done(await CarryOutAsync(heard, player, playing, new("loop", intent, null, "loop")));
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
