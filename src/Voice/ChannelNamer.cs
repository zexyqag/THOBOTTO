using Microsoft.EntityFrameworkCore;

using NetCord;
using NetCord.Gateway;
using NetCord.Rest;

using THOBOTTO.Data;
using THOBOTTO.Modules;

namespace THOBOTTO.Voice;

// Names hub channels after what the people in them do, as the server's voice settings choose.
public sealed class ChannelNamer(
    GatewayClient gateway,
    RestClient rest,
    VoicePresence presence,
    IDbContextFactory<BotDbContext> dbFactory,
    ModuleState modules,
    SettingsStore settings,
    TimeProvider time,
    ILogger<ChannelNamer> logger) : BackgroundService
{
    // A name must hold this long before the channel takes it, so a quick switch renames nothing.
    private static readonly TimeSpan Hold = TimeSpan.FromMinutes(2);
    // Discord allows two renames of a channel in ten minutes; past that the request would wait.
    private static readonly TimeSpan RenameWindow = TimeSpan.FromMinutes(10);
    private const int RenamesPerWindow = 2;
    private const int MaxName = 100;

    // Channel → the name it should get, since when.
    private readonly Dictionary<ulong, (string Name, DateTimeOffset Since)> _wanted = [];
    private readonly Dictionary<ulong, List<DateTimeOffset>> _renamed = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30), time);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await using var db = await dbFactory.CreateDbContextAsync(stoppingToken);
            var channels = await db.DynamicVoiceChannels.AsNoTracking().ToListAsync(stoppingToken);
            foreach (var gone in _wanted.Keys.Concat(_renamed.Keys).Except(channels.Select(c => c.ChannelId)).ToList())
            {
                _wanted.Remove(gone);
                _renamed.Remove(gone);
            }
            foreach (var guild in channels.GroupBy(c => c.GuildId))
            {
                try
                {
                    await NameAsync(guild.Key, guild.ToList(), stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Naming voice channels in guild {GuildId} failed", guild.Key);
                }
            }
        }
    }

    private async Task NameAsync(ulong guildId, List<DynamicVoiceChannel> channels, CancellationToken ct)
    {
        if (!gateway.Cache.Guilds.TryGetValue(guildId, out var guild) || !await modules.IsEnabledAsync(guildId, DynamicVoice.ModuleId))
            return;
        var byActivity = (await settings.GetAsync<VoiceRules>(guildId, DynamicVoice.ModuleId)).Naming == ChannelNaming.Activity;
        var people = presence.Snapshot(guildId).Where(p => !p.Value.IsBot).ToLookup(p => p.Value.ChannelId, p => p.Key);
        var now = time.GetUtcNow();
        foreach (var channel in channels)
        {
            if (!guild.Channels.TryGetValue(channel.ChannelId, out var current))
                continue;
            var name = byActivity && MajorityGame(people[channel.ChannelId].Select(user => Games(guild, user))) is { } game
                ? Trim($"🎮 {game}")
                : channel.Name;
            if (current.Name == name)
            {
                _wanted.Remove(channel.ChannelId);
                continue;
            }
            if (!_wanted.TryGetValue(channel.ChannelId, out var wanted) || wanted.Name != name)
            {
                _wanted[channel.ChannelId] = (name, now);
                continue;
            }
            var renamed = _renamed.TryGetValue(channel.ChannelId, out var times) ? times : _renamed[channel.ChannelId] = [];
            renamed.RemoveAll(t => now - t >= RenameWindow);
            if (now - wanted.Since < Hold || renamed.Count >= RenamesPerWindow)
                continue;
            renamed.Add(now);
            await rest.ModifyGuildChannelAsync(channel.ChannelId, o => o.Name = name, cancellationToken: ct);
        }
    }

    private static IEnumerable<string> Games(Guild guild, ulong userId)
        => guild.Presences.TryGetValue(userId, out var p) ? p.Activities.Where(a => a.Type == UserActivityType.Playing).Select(a => a.Name) : [];

    // The game played by at least half the people (the most played, then by name), if any.
    public static string? MajorityGame(IEnumerable<IEnumerable<string>> playing)
    {
        var people = playing.Select(games => games.Distinct().ToList()).ToList();
        return people.SelectMany(g => g).GroupBy(g => g)
            .Where(g => g.Count() * 2 >= people.Count)
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => g.Key).FirstOrDefault();
    }

    private static string Trim(string name) => name.Length <= MaxName ? name : name[..MaxName];
}
