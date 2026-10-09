using NetCord;
using NetCord.Gateway;
using NetCord.Rest;

using THOBOTTO.Data;
using THOBOTTO.Helpers;
using THOBOTTO.Music;

using Microsoft.EntityFrameworkCore;

namespace THOBOTTO.Listening;

// Carries out what members say to the helper playing in their channel ("Jeeves, skip"), as the slash
// command would, with the same permissions; the helper answers in the channel's chat.
public sealed class VoiceCommands(
    VoiceEars ears,
    MusicService music,
    PersonalityBook personalities,
    GatewayClient gateway,
    RestClient rest,
    IDbContextFactory<BotDbContext> dbFactory,
    TimeProvider time,
    ILogger<VoiceCommands> logger) : IHostedService
{
    private const int VolumeStep = 20;

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
        if (music.PlayerIn(heard.GuildId, heard.ChannelId) is not { } player)
            return;
        // The helper in that very channel answers (one playing along answers for itself).
        var helper = player.Mirrors.FirstOrDefault(m => m.VoiceChannelId == heard.ChannelId)?.Helper ?? player.Helper;
        var name = await personalities.NameAsync(heard.GuildId, helper);
        if (VoiceCommandParser.Parse(heard.Text, [name]) is not { } command)
            return;

        string reply;
        try
        {
            reply = await CarryOutAsync(heard, player, helper, command);
        }
        catch (Exception ex) when (ex is RestException or HttpRequestException or InvalidOperationException)
        {
            logger.LogWarning("A voice command failed: {Message}", ex.Message);
            reply = "That didn't work just now; try again.";
        }
        await music.ReplyAsync(heard.GuildId, helper, heard.ChannelId, $"🎙️ <@{heard.UserId}> · {reply}");
        if (command.Intent != VoiceIntent.Unknown)
            await AuditAsync(heard, command);
    }

    private async Task<string> CarryOutAsync(Heard heard, MusicPlayer player, HelperBot helper, VoiceCommand command)
    {
        var guildId = heard.GuildId;
        switch (command.Intent)
        {
            case VoiceIntent.Play:
                return await music.PlayAsync(guildId, heard.UserId, heard.ChannelId, player.TextChannelId, command.Argument!);
            case VoiceIntent.NowPlaying:
                return player.Current is { } current ? $"🎵 {current.Markdown} · {current.Length}" : "Nothing is playing.";
            case VoiceIntent.Unknown:
                return $"I didn't get that: “{command.Said}”. Try “play …”, “skip”, “pause”, “louder” or “what's playing”.";
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

    // The helper posts its reply itself, so its line goes without its name in front.
    private Task<string> LineAsync(ulong guildId, HelperBot helper, string moment)
        => personalities.SayAsync(guildId, helper, moment, new Dictionary<string, string>());

    private async Task<GuildUser> MemberAsync(ulong guildId, ulong userId)
        => gateway.Cache.Guilds.TryGetValue(guildId, out var guild) && guild.Users.TryGetValue(userId, out var cached) ? cached : await rest.GetGuildUserAsync(guildId, userId);

    // What was done, not what was said.
    private async Task AuditAsync(Heard heard, VoiceCommand command)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        db.AuditEntries.Add(new()
        {
            GuildId = heard.GuildId,
            ActorId = heard.UserId,
            Action = "voice.command",
            Details = command.Argument is { } argument ? $"{command.Intent} {argument}" : command.Intent.ToString(),
            CreatedAt = time.GetUtcNow(),
        });
        await db.SaveChangesAsync();
    }
}
