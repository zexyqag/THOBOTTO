using System.Collections.Concurrent;

using NetCord.Gateway;
using NetCord.Gateway.Voice;

using THOBOTTO.Discord;

namespace THOBOTTO.Helpers;

// A free helper (not playing music, not listening, in no call of the server) hops into a voice channel to play
// something, a sound or a line, and leaves again.
public sealed class HelperHops(HelperFleet fleet, IEnumerable<IHelperWork> works, ListeningSeats seats, VoicePresence presence, VoiceMouths mouths, ILogger<HelperHops> logger)
{
    private static readonly TimeSpan Slack = TimeSpan.FromSeconds(5);
    private readonly ConcurrentDictionary<ulong, byte> _hopping = new();

    // The sound is made for the helper that comes (a line in its voice); null when there's nothing to play after all.
    public async Task<bool> PlayAsync(ulong guildId, ulong channelId, Func<HelperBot, Task<(short[] Stereo, TimeSpan Length)?>> sound, string what)
    {
        var present = presence.Snapshot(guildId);
        var helper = fleet.Helpers.FirstOrDefault(h => h.InGuild(guildId) && !works.Any(w => w.Busy(h.UserId, guildId)) && !seats.IsListening(guildId, h.UserId)
            && !present.ContainsKey(h.UserId) && _hopping.TryAdd(h.UserId, 0));
        if (helper is null)
            return false;
        VoiceClient? client = null;
        try
        {
            if (await sound(helper) is not { } made)
                return false;
            client = await helper.Gateway.JoinVoiceChannelAsync(guildId, channelId, new VoiceClientConfiguration()).WaitAsync(Slack);
            await client.StartAsync();
            await client.EnterSpeakingStateAsync(new SpeakingProperties(SpeakingFlags.Microphone));
            await mouths.Of(client).SpeakAsync(made.Stereo, dip: false).WaitAsync(made.Length + Slack);
            logger.LogInformation("{Helper} hopped into {ChannelId} of {GuildId} to play {What}", helper.Name, channelId, guildId, what);
            return true;
        }
        finally
        {
            if (client is not null)
            {
                mouths.Forget(client);
                client.Dispose();
                await helper.Gateway.UpdateVoiceStateAsync(new VoiceStateProperties(guildId, null));
            }
            _hopping.TryRemove(helper.UserId, out _);
        }
    }
}
