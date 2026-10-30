using System.Collections.Concurrent;
using System.Security.Cryptography;

using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

using THOBOTTO.Helpers;
using THOBOTTO.Voice.Relay;

namespace THOBOTTO.Voice.Speaking;

// Music's side of a helper speaking where it plays without a voice connection of its own (not through the relay):
// its words go as a short track in the music's place, which goes on after.
public interface IClipPlayer
{
    // The helper playing there; null when none.
    HelperBot? PlayingIn(ulong guildId, ulong voiceChannelId);

    // Plays the clip at that address in the music's place and waits for it to end; false when it couldn't.
    Task<bool> PlayClipAsync(ulong guildId, ulong voiceChannelId, string url, TimeSpan length);
}

// Spoken lines kept a couple of minutes for Lavalink to fetch at /speech/<id>, and the player that plays them
// (set by Music at its start: it needs speech itself, so it can't be asked for here).
public sealed class SpeechClips(VoiceRelay relay, IServer server, TimeProvider time)
{
    private static readonly TimeSpan Keep = TimeSpan.FromMinutes(2);

    public IClipPlayer? Player { get; set; }
    private readonly ConcurrentDictionary<string, (byte[] Wav, DateTimeOffset Until)> _clips = new();

    public string Add(Spoken spoken)
    {
        foreach (var (id, clip) in _clips.Where(c => c.Value.Until < time.GetUtcNow()).ToList())
            _clips.TryRemove(id, out _);
        var fresh = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(12));
        _clips[fresh] = (Pcm.Wav(spoken), time.GetUtcNow() + Keep);
        return fresh;
    }

    public byte[]? Get(string id) => _clips.TryGetValue(id, out var clip) && clip.Until >= time.GetUtcNow() ? clip.Wav : null;

    // Where Lavalink fetches a clip: the bot by the name the relay's traffic reaches it on, on this server's port.
    public string UrlFor(string id) => $"http://{relay.Host}:{Port}/speech/{id}";

    private int Port => server.Features.Get<IServerAddressesFeature>()?.Addresses
        .Select(a => Uri.TryCreate(a.Replace("[::]", "localhost").Replace("+", "localhost").Replace("*", "localhost"), UriKind.Absolute, out var uri) ? uri.Port : 0)
        .FirstOrDefault(p => p > 0) ?? 80;
}
