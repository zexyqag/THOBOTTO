using NetCord.Gateway;
using NetCord.Gateway.Voice;

using THOBOTTO.Modules;

namespace THOBOTTO.Speaking;

public enum SpeechKind
{
    Reply,
    Question,
    NowPlaying,
    JoinAndLeave,
}

// Says a line out loud in a voice channel, where a helper there can (one playing through the relay, or one
// listening, unmuted while it talks) and the server's voices module wants that kind of line spoken.
public sealed partial class HelperSpeech(HelperVoices voices, VoiceMouths mouths, ModuleState modules, SettingsStore settings, GatewayClient gateway, TimeProvider time, ILogger<HelperSpeech> logger)
{
    // However long the speech, it's given up on after this much longer.
    private static readonly TimeSpan Slack = TimeSpan.FromSeconds(5);

    public async Task SayAsync(ulong guildId, ulong channelId, string text, SpeechKind kind)
    {
        if (!voices.On || mouths.In(guildId, channelId) is not { } seat || !await WantedAsync(guildId, kind))
            return;
        try
        {
            var started = time.GetTimestamp();
            if (await voices.SayAsync(guildId, seat.Helper, Named(guildId, text)) is not { } spoken)
                return;
            logger.LogInformation("Text to speech: {Seconds:0.0} s of speech in {Ms} ms", spoken.Length.TotalSeconds, (int)time.GetElapsedTime(started).TotalMilliseconds);
            var mouth = mouths.Of(seat.Client);
            if (seat.Muted)
            {
                await seat.Helper.Gateway.UpdateVoiceStateAsync(new VoiceStateProperties(guildId, channelId) { SelfMute = false });
                await seat.Client.EnterSpeakingStateAsync(new SpeakingProperties(SpeakingFlags.Microphone));
            }
            // After whatever is still being said.
            var ahead = mouth.Queued;
            await mouth.SpeakAsync(Pcm.ToDiscord(spoken)).WaitAsync(ahead + spoken.Length + Slack);
            if (seat.Muted && !mouth.Speaking && mouths.In(guildId, channelId) == seat)
                await seat.Helper.Gateway.UpdateVoiceStateAsync(new VoiceStateProperties(guildId, channelId) { SelfMute = true });
        }
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException or ObjectDisposedException or System.Net.WebSockets.WebSocketException)
        {
            logger.LogWarning("Speaking in {ChannelId} failed: {Error}", channelId, ex.GetType().Name + ": " + ex.Message);
        }
    }

    // Mentions as the names people go by there.
    private string Named(ulong guildId, string text)
        => UserMention().Replace(text, m => gateway.Cache.Guilds.TryGetValue(guildId, out var guild) && guild.Users.TryGetValue(ulong.Parse(m.Groups[1].Value), out var user)
            ? user.Nickname ?? user.GlobalName ?? user.Username
            : "someone");

    [System.Text.RegularExpressions.GeneratedRegex(@"<@!?(\d+)>")]
    private static partial System.Text.RegularExpressions.Regex UserMention();

    private async Task<bool> WantedAsync(ulong guildId, SpeechKind kind)
    {
        if (!await modules.IsEnabledAsync(guildId, HelperVoices.ModuleId))
            return false;
        var rules = await settings.GetAsync<SpeechRules>(guildId, HelperVoices.ModuleId);
        return kind switch
        {
            SpeechKind.Reply => rules.Replies,
            SpeechKind.Question => rules.Questions,
            SpeechKind.NowPlaying => rules.NowPlaying,
            _ => rules.JoinAndLeave,
        };
    }
}
