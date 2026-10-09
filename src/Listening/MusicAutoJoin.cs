using NetCord.Gateway;

using THOBOTTO.Modules;
using THOBOTTO.Music;
using THOBOTTO.Voice;

namespace THOBOTTO.Listening;

// Members who have a helper join them for music: when they come into a voice channel without music, a free
// helper joins and waits for something to play (and leaves after the idle time, as any helper with nothing on).
// Once per visit: it doesn't come back until they've left the channel and come in again.
public sealed class MusicAutoJoin(GatewayClient gateway, MusicService music, VoicePrefs prefs, ModuleState modules, SettingsStore settings, VoicePresence presence, TimeProvider time, ILogger<MusicAutoJoin> logger)
    : BackgroundService
{
    private static readonly TimeSpan Check = TimeSpan.FromSeconds(5);

    // (server, member) → the channel a helper already came to on this visit.
    private readonly Dictionary<(ulong Guild, ulong User), ulong> _visits = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Check, time);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                var all = await prefs.AllAsync();
                foreach (var guild in gateway.Cache.Guilds.Values.ToList())
                    await CheckAsync(guild.Id, all);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Joining members for music failed");
            }
        }
    }

    private async Task CheckAsync(ulong guildId, IReadOnlyDictionary<(ulong Guild, ulong User), VoicePreference> all)
    {
        var present = presence.Snapshot(guildId);
        foreach (var gone in _visits.Keys.Where(k => k.Guild == guildId && (!present.TryGetValue(k.User, out var p) || p.ChannelId != _visits[k])).ToList())
            _visits.Remove(gone);
        if (!await modules.IsEnabledAsync(guildId, MusicService.ModuleId) || !(await settings.GetAsync<MusicRules>(guildId, MusicService.ModuleId)).AutoJoinAllowed)
            return;

        foreach (var (userId, where) in present.Where(p => !p.Value.IsBot && !p.Value.Deafened))
        {
            if (all.GetValueOrDefault((guildId, userId)) is not { AutoMusic: true } || _visits.ContainsKey((guildId, userId)))
                continue;
            _visits[(guildId, userId)] = where.ChannelId;
            if (music.PlayerIn(guildId, where.ChannelId) is null)
                await music.PlayerForAsync(guildId, where.ChannelId, where.ChannelId);
        }
    }
}
