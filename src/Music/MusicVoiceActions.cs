using NetCord;
using NetCord.Gateway;
using NetCord.Rest;

using THOBOTTO.Helpers;
using THOBOTTO.Voice.Commands;

namespace THOBOTTO.Music;

// The music commands by voice ("skip", "throw on some ABBA after this"), done as the slash commands would, with
// the same permissions, on the player in the speaker's channel.
public sealed class MusicVoiceActions(MusicService music, PersonalityBook personalities, GatewayClient gateway, RestClient rest) : IVoiceActions
{
    private const int VolumeStep = 20;

    private enum Intent
    {
        Play,
        Queue,
        QueueFirst,
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

    public IEnumerable<VoiceAction> Actions
    {
        get
        {
            VoiceAction Simple(string name, string description, Intent intent)
                => new(name, description, [], [], (request, _) => CarryOutAsync(request, intent, null));
            return
            [
                new("play", "Play or queue a song or playlist. when: now only when they say play or put on (it skips what plays); first for \"next\" or \"after this\"; last for queue or add, words that sound like them (\"q\", \"cue\", \"at\"), or when unsure.",
                    new() { ["query"] = Schema.Text("What to search for, or a link"), ["when"] = Schema.OneOf("When it plays", "now", "first", "last") }, ["query", "when"],
                    (request, args) => CarryOutAsync(request,
                        Schema.String(args, "when") switch { "first" => Intent.QueueFirst, "last" => Intent.Queue, _ => Intent.Play },
                        Schema.String(args, "query") ?? ""),
                    ("throw on some abba after this one", """{"query":"abba","when":"first"}""")),
                Simple("skip", "Skip the song playing.", Intent.Skip),
                Simple("pause", "Pause the music.", Intent.Pause),
                Simple("resume", "Carry on with the music.", Intent.Resume),
                Simple("stop", "Stop the music and send the helper away.", Intent.Stop),
                Simple("now_playing", "Say which song is playing.", Intent.NowPlaying),
                Simple("shuffle", "Shuffle the queue.", Intent.Shuffle),
                new("volume", "Change the volume: up, down, or to a level (0-200).",
                    new() { ["change"] = Schema.OneOf("Which way", "up", "down", "to"), ["level"] = Schema.Number("The level, for \"to\"") }, ["change"],
                    (request, args) =>
                    {
                        var (intent, level) = Schema.Int(args, "level") is { } n ? (Intent.Volume, n.ToString())
                            : Schema.String(args, "change") == "down" ? (Intent.Quieter, null)
                            : (Intent.Louder, (string?)null);
                        return CarryOutAsync(request, intent, level);
                    }),
                new("loop", "Loop the song, the queue, or stop looping.",
                    new() { ["what"] = Schema.OneOf("What to loop", "song", "queue", "off") }, ["what"],
                    (request, args) => CarryOutAsync(request,
                        Schema.String(args, "what") switch { "song" => Intent.LoopTrack, "queue" => Intent.LoopQueue, _ => Intent.LoopOff }, null)),
            ];
        }
    }

    private async Task<VoicePlan> CarryOutAsync(VoiceRequest request, Intent intent, string? argument)
        => VoicePlan.Done(await DoAsync(request, intent, argument));

    private async Task<string> DoAsync(VoiceRequest request, Intent intent, string? argument)
    {
        var heard = request.Heard;
        var guildId = heard.GuildId;
        var player = music.PlayerIn(guildId, heard.ChannelId);
        if (intent is Intent.Play or Intent.Queue or Intent.QueueFirst)
        {
            var placement = intent switch { Intent.Queue => Placement.Last, Intent.QueueFirst => Placement.First, _ => Placement.Now };
            return await music.PlayAsync(guildId, heard.UserId, heard.ChannelId, player?.TextChannelId ?? heard.ChannelId, argument!, placement);
        }
        if (player is null)
            return "Nothing is playing here. Say “play” and what.";
        if (intent == Intent.NowPlaying)
            return player.Current is { } current ? $"🎵 {current.Markdown} · {current.Length}" : "Nothing is playing.";

        // The helper playing in that channel: its lines answer.
        var helper = player.Mirrors.FirstOrDefault(m => m.VoiceChannelId == heard.ChannelId)?.Helper ?? player.Helper;
        var user = await MemberAsync(guildId, heard.UserId);
        if (intent == Intent.Skip)
        {
            if (await music.RefusalAsync(guildId, user, player, ownTrackAllowed: true) is not null)
                return await music.VoteSkipAsync(player, heard.UserId);
            await player.SkipAsync();
            return await LineAsync(guildId, helper, Moments.Skipped);
        }
        if (await music.RefusalAsync(guildId, user, player) is { } refusal)
            return refusal;

        switch (intent)
        {
            case Intent.Pause:
                await player.SetPausedAsync(true);
                return "⏸️ Paused.";
            case Intent.Resume:
                await player.SetPausedAsync(false);
                return "▶️ Resumed.";
            case Intent.Stop:
                await player.StopAsync();
                await music.DisconnectAsync(player);
                return await LineAsync(guildId, helper, Moments.Stopped);
            case Intent.Louder or Intent.Quieter or Intent.Volume:
                if (await music.VolumeRefusalAsync(guildId, user, player) is { } notYours)
                    return notYours;
                var volume = intent switch
                {
                    Intent.Louder => player.Volume + VolumeStep,
                    Intent.Quieter => player.Volume - VolumeStep,
                    _ => int.Parse(argument!),
                };
                volume = Math.Clamp(volume, 0, 200);
                await player.SetVolumeAsync(volume);
                return $"🔊 Volume {volume}%.";
            case Intent.Shuffle:
                await player.ShuffleAsync();
                return "🔀 Shuffled.";
            default:
                player.Loop = intent switch { Intent.LoopTrack => LoopMode.Track, Intent.LoopQueue => LoopMode.Queue, _ => LoopMode.Off };
                return player.Loop == LoopMode.Off ? "🔁 Not looping." : $"🔁 Looping the {player.Loop.ToString().ToLowerInvariant()}.";
        }
    }

    // The helper posts its reply itself, so its line goes without its name in front.
    private Task<string> LineAsync(ulong guildId, HelperBot helper, string moment)
        => personalities.SayAsync(guildId, helper, moment, new Dictionary<string, string>());

    private async Task<GuildUser> MemberAsync(ulong guildId, ulong userId)
        => gateway.Cache.Guilds.TryGetValue(guildId, out var guild) && guild.Users.TryGetValue(userId, out var cached) ? cached : await rest.GetGuildUserAsync(guildId, userId);
}
