using System.Collections.Concurrent;

using NetCord.Gateway.Voice;

namespace THOBOTTO.Speaking;

// What one voice connection sends: music frames from the relay pass straight through, or with speech mixed in
// (the music dipped); speech goes alone when no music comes. It numbers every packet itself, so the two fit.
public sealed class VoiceMouth : IDisposable
{
    // 20 ms of 48 kHz stereo.
    private const int FrameSamples = 960;
    private const int FrameShorts = FrameSamples * 2;
    private const float MusicUnderSpeech = 0.3f;
    private static readonly TimeSpan MusicGap = TimeSpan.FromMilliseconds(60);

    private readonly VoiceClient _client;
    private readonly TimeProvider _time;
    private readonly OpusEncoder _encoder = new(VoiceChannels.Stereo, OpusApplication.Audio);
    private readonly OpusDecoder _decoder = new(VoiceChannels.Stereo);
    private readonly Lock _gate = new();
    // 20 ms frames of speech (or a sound); the last of each says when it's been said. Dip: the music goes down
    // under it (speech), or stays (sounds).
    private readonly Queue<(short[] Frame, TaskCompletionSource? Said, bool Dip)> _speech = new();
    private readonly CancellationTokenSource _life = new();
    private readonly byte[] _packet = new byte[4000];
    private readonly short[] _music = new short[FrameShorts];
    private ushort _sequence;
    private uint _timestamp;
    private long _musicAt;

    public VoiceMouth(VoiceClient client, TimeProvider time)
    {
        (_client, _time) = (client, time);
        _ = PaceAsync();
    }

    // How much speech is waiting to go.
    public TimeSpan Queued
    {
        get
        {
            lock (_gate)
                return TimeSpan.FromMilliseconds(_speech.Count * 20);
        }
    }

    public bool Speaking
    {
        get
        {
            lock (_gate)
                return _speech.Count > 0;
        }
    }

    // A frame from the music (Opus, 20 ms).
    public void Music(ReadOnlySpan<byte> frame)
    {
        lock (_gate)
        {
            _musicAt = _time.GetTimestamp();
            if (_speech.Count == 0)
            {
                Send(frame);
                return;
            }
            var decoded = _decoder.Decode(frame, _music, FrameSamples, false);
            var (speech, said, dip) = _speech.Dequeue();
            var under = dip ? MusicUnderSpeech : 1f;
            for (var i = 0; i < FrameShorts; i++)
                _music[i] = (short)Math.Clamp((i < decoded * 2 ? _music[i] * under : 0) + speech[i], short.MinValue, short.MaxValue);
            Send(_packet.AsSpan(0, _encoder.Encode(_music, FrameSamples, _packet)));
            said?.TrySetResult();
        }
    }

    // Says it (48 kHz stereo); done when it's all been sent.
    public Task SpeakAsync(short[] stereo, bool dip = true)
    {
        var said = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (stereo.Length == 0)
            return Task.CompletedTask;
        lock (_gate)
        {
            for (var at = 0; at < stereo.Length; at += FrameShorts)
            {
                var frame = new short[FrameShorts];
                stereo.AsSpan(at, Math.Min(FrameShorts, stereo.Length - at)).CopyTo(frame);
                _speech.Enqueue((frame, at + FrameShorts >= stereo.Length ? said : null, dip));
            }
        }
        return said.Task;
    }

    // Without music coming, speech goes on its own, a frame every 20 ms.
    private async Task PaceAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(20), _time);
        try
        {
            while (await timer.WaitForNextTickAsync(_life.Token))
            {
                lock (_gate)
                {
                    if (_speech.Count == 0 || _time.GetElapsedTime(_musicAt) < MusicGap)
                        continue;
                    var (speech, said, _) = _speech.Dequeue();
                    Send(_packet.AsSpan(0, _encoder.Encode(speech, FrameSamples, _packet)));
                    said?.TrySetResult();
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void Send(ReadOnlySpan<byte> frame)
    {
        try
        {
            _client.SendVoice(_sequence++, _timestamp, frame);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
        }
        _timestamp += FrameSamples;
    }

    public void Dispose()
    {
        _life.Cancel();
        lock (_gate)
        {
            foreach (var (_, said, _) in _speech)
                said?.TrySetResult();
            _speech.Clear();
        }
        _encoder.Dispose();
        _decoder.Dispose();
    }
}

// A helper's connection that can speak in a channel; a listening one is muted until it does.
public sealed record VoiceSeat(Helpers.HelperBot Helper, VoiceClient Client, bool Muted);

// Each voice connection's mouth, made when it first speaks (or plays through the relay) and gone with it; and
// which connection speaks in which channel (a helper playing through the relay, else one listening).
public sealed class VoiceMouths(TimeProvider time)
{
    private readonly ConcurrentDictionary<VoiceClient, VoiceMouth> _mouths = new();
    private readonly ConcurrentDictionary<(ulong Guild, ulong Channel), VoiceSeat> _seats = new();

    public VoiceMouth Of(VoiceClient client) => _mouths.GetOrAdd(client, c => new(c, time));

    public void Seat(ulong guildId, ulong channelId, VoiceSeat seat) => _seats[(guildId, channelId)] = seat;

    public void Unseat(VoiceClient client)
    {
        foreach (var (key, _) in _seats.Where(s => s.Value.Client == client).ToList())
            _seats.TryRemove(key, out _);
    }

    public VoiceSeat? In(ulong guildId, ulong channelId) => _seats.GetValueOrDefault((guildId, channelId));

    public void Forget(VoiceClient client)
    {
        Unseat(client);
        if (_mouths.TryRemove(client, out var mouth))
            mouth.Dispose();
    }
}
