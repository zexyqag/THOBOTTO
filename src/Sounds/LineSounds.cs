using System.Collections.Concurrent;

using Microsoft.EntityFrameworkCore;

using THOBOTTO.Data;
using THOBOTTO.Discord;
using THOBOTTO.Helpers;
using THOBOTTO.Modules;

namespace THOBOTTO.Sounds;

// A sound for a helper's line ([sound name]): the library's by name, as /sound play finds it, else one of Discord's
// default sounds. On its own because the board speaks through the helpers, and the helpers' voices look here.
public sealed class LineSounds(IDbContextFactory<BotDbContext> dbFactory, ModuleState modules, DiscordApi discord, TimeProvider time) : ISoundSource
{
    private static readonly TimeSpan DefaultsFresh = TimeSpan.FromHours(1);
    private (IReadOnlyList<SoundboardSound> Sounds, DateTimeOffset At)? _defaults;
    private readonly ConcurrentDictionary<ulong, Spoken> _defaultClips = new();

    public async Task<bool> HasAsync(ulong guildId, string name)
        => await LibraryAsync(guildId, name) is not null || await DefaultAsync(name) is not null;

    public async Task<Spoken?> SoundAsync(ulong guildId, string name, CancellationToken ct)
    {
        if (await LibraryAsync(guildId, name) is { Audio: { } audio } sound)
            return SoundDecoder.Decode(audio, sound.FileName);
        if (await DefaultAsync(name) is not { } remote)
            return null;
        if (_defaultClips.TryGetValue(remote.Id, out var known))
            return known;
        if (await discord.SoundFileAsync(remote.Id) is not { } bytes || SoundDecoder.Decode(bytes, bytes.AsSpan().StartsWith("OggS"u8) ? "default.ogg" : "default.mp3") is not { } decoded)
            return null;
        return _defaultClips[remote.Id] = decoded;
    }

    private async Task<Sound?> LibraryAsync(ulong guildId, string name)
    {
        if (guildId == 0 || !await modules.IsEnabledAsync(guildId, SoundBoard.ModuleId))
            return null;
        await using var db = await dbFactory.CreateDbContextAsync();
        return SoundBoard.Pick(await db.Sounds.AsNoTracking().Where(s => s.GuildId == guildId && s.State == SoundStates.Library).ToListAsync(), name);
    }

    private async Task<SoundboardSound?> DefaultAsync(string name)
    {
        if (_defaults is not { } listed || time.GetUtcNow() - listed.At > DefaultsFresh)
        {
            if (await discord.DefaultSoundsAsync() is not { } sounds)
                return null;
            _defaults = listed = (sounds, time.GetUtcNow());
        }
        return listed.Sounds.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
    }
}
