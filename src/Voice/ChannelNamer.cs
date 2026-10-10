using System.Collections.Concurrent;

using Microsoft.EntityFrameworkCore;

using NetCord;
using NetCord.Gateway;
using NetCord.Rest;

using THOBOTTO.Data;
using THOBOTTO.Helpers;
using THOBOTTO.Modules;
using THOBOTTO.Music;

namespace THOBOTTO.Voice;

// What's going on in a channel, for its name.
public sealed record ChannelScene(string Default, string? Pinned, string? Game, bool Live, string? Music, bool Quiet);

// Names hub channels: what their owner named them, else (as the server's voice settings choose) after what the
// people in them do.
public sealed class ChannelNamer(
    GatewayClient gateway,
    RestClient rest,
    VoicePresence presence,
    MusicService music,
    PersonalityBook personalities,
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
    // Channels whose owner just named them: no waiting.
    private readonly ConcurrentDictionary<ulong, byte> _now = new();

    public void RenameSoon(ulong channelId) => _now[channelId] = 0;

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
        var people = presence.Snapshot(guildId).Where(p => !p.Value.IsBot).ToLookup(p => p.Value.ChannelId);
        var now = time.GetUtcNow();
        foreach (var channel in channels)
        {
            if (!guild.Channels.TryGetValue(channel.ChannelId, out var current))
                continue;
            var here = people[channel.ChannelId].ToList();
            var playing = music.PlayerIn(guildId, channel.ChannelId) is { Current: { } track } player
                ? $"{await personalities.NameAsync(guildId, player.Mirrors.FirstOrDefault(m => m.VoiceChannelId == channel.ChannelId)?.Helper ?? player.Helper)} · {track.Author}"
                : null;
            var scene = new ChannelScene(channel.Name, channel.PinnedName,
                MajorityGame(here.Select(p => Games(guild, p.Key))),
                here.Any(p => p.Value.Streaming), playing, here.Count > 0 && here.All(p => p.Value.Deafened));
            var name = Trim(NameFor(scene, byActivity));
            var hurry = _now.TryRemove(channel.ChannelId, out _);
            if (current.Name == name)
            {
                _wanted.Remove(channel.ChannelId);
                continue;
            }
            if (!_wanted.TryGetValue(channel.ChannelId, out var wanted) || wanted.Name != name)
            {
                _wanted[channel.ChannelId] = wanted = (name, hurry ? DateTimeOffset.MinValue : now);
                if (!hurry)
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

    // The owner's name first ({game} filled in, or their channel's usual name when nobody plays one); then,
    // going by activity: streaming, the game most play, the music, everyone deafened.
    public static string NameFor(ChannelScene scene, bool byActivity)
    {
        if (scene.Pinned is { } pinned)
            return !pinned.Contains("{game}") ? pinned : scene.Game is { } played ? pinned.Replace("{game}", played) : scene.Default;
        if (!byActivity)
            return scene.Default;
        return scene switch
        {
            { Live: true, Game: { } game } => $"🔴 {game}",
            { Live: true } => "🔴 Live",
            { Game: { } game } => $"🎮 {game}",
            { Music: { } playing } => $"🎵 {playing}",
            { Quiet: true } => "💤 Quiet",
            _ => scene.Default,
        };
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
