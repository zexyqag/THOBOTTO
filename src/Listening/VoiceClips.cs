using Microsoft.EntityFrameworkCore;

using THOBOTTO.Data;

namespace THOBOTTO.Listening;

// Clips of members' own voice, kept only while they've asked for it (on their Me page), for them to label with
// what they really said: a test set for comparing speech to text on real voices. Only they see theirs.
public sealed class VoiceClips(IDbContextFactory<BotDbContext> dbFactory, VoicePrefs prefs, TimeProvider time, ILogger<VoiceClips> logger)
{
    public const int Kept = 300;

    public async Task KeepAsync(ulong guildId, ulong userId, float[] audio, string heard)
    {
        if (!(await prefs.GetAsync(guildId, userId)).KeepClips)
            return;
        try
        {
            var pcm = new byte[audio.Length * 2];
            for (var i = 0; i < audio.Length; i++)
                BitConverter.TryWriteBytes(pcm.AsSpan(i * 2), (short)Math.Clamp(audio[i] * 32767, short.MinValue, short.MaxValue));
            await using var db = await dbFactory.CreateDbContextAsync();
            db.VoiceClips.Add(new() { GuildId = guildId, UserId = userId, At = time.GetUtcNow(), Audio = pcm, Heard = heard });
            await db.SaveChangesAsync();
            var old = await db.VoiceClips.Where(c => c.GuildId == guildId && c.UserId == userId).OrderByDescending(c => c.At).Skip(Kept).Select(c => c.Id).ToListAsync();
            if (old.Count > 0)
                await db.VoiceClips.Where(c => old.Contains(c.Id)).ExecuteDeleteAsync();
        }
        catch (DbUpdateException ex)
        {
            logger.LogWarning("Keeping a voice clip: {Message}", ex.Message);
        }
    }

    // Newest first, without the audio.
    public async Task<IReadOnlyList<VoiceClip>> ListAsync(ulong guildId, ulong userId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.VoiceClips.AsNoTracking().Where(c => c.GuildId == guildId && c.UserId == userId).OrderByDescending(c => c.At)
            .Select(c => new VoiceClip { Id = c.Id, GuildId = c.GuildId, UserId = c.UserId, At = c.At, Audio = Array.Empty<byte>(), Heard = c.Heard, Said = c.Said, NotForHelper = c.NotForHelper })
            .ToListAsync();
    }

    // With the audio, for its owner.
    public async Task<VoiceClip?> GetAsync(ulong userId, long id)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.VoiceClips.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id && c.UserId == userId);
    }

    public async Task LabelAsync(ulong userId, long id, string? said, bool notForHelper)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.VoiceClips.Where(c => c.Id == id && c.UserId == userId)
            .ExecuteUpdateAsync(c => c.SetProperty(x => x.Said, string.IsNullOrWhiteSpace(said) ? null : said.Trim()).SetProperty(x => x.NotForHelper, notForHelper));
    }

    public async Task DeleteAsync(ulong guildId, ulong userId, long? id)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.VoiceClips.Where(c => c.GuildId == guildId && c.UserId == userId && (id == null || c.Id == id)).ExecuteDeleteAsync();
    }

    // A WAV file of a clip, to listen to in the browser.
    public static byte[] Wav(VoiceClip clip)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8);
        writer.Write(36 + clip.Audio.Length);
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(16_000);
        writer.Write(16_000 * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(clip.Audio.Length);
        writer.Write(clip.Audio);
        return stream.ToArray();
    }
}
