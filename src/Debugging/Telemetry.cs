using System.Collections.Concurrent;

namespace THOBOTTO.Debugging;

// Running counts for the debug page: open Activity windows, and what video streaming sends out.
public sealed class Telemetry(TimeProvider time)
{
    private readonly ConcurrentDictionary<ulong, int> _activities = new();
    private readonly ConcurrentQueue<(DateTimeOffset At, long Bytes)> _sent = new();
    private long _videoBytes;

    public long VideoBytes => Interlocked.Read(ref _videoBytes);

    // Voice channel → open Activity windows.
    public IReadOnlyDictionary<ulong, int> Activities => _activities.Where(a => a.Value > 0).ToDictionary();

    public IDisposable Opened(ulong channelId)
    {
        _activities.AddOrUpdate(channelId, 1, (_, n) => n + 1);
        return new Closing(() => _activities.AddOrUpdate(channelId, 0, (_, n) => Math.Max(0, n - 1)));
    }

    public void VideoSent(long bytes)
    {
        Interlocked.Add(ref _videoBytes, bytes);
        _sent.Enqueue((time.GetUtcNow(), bytes));
    }

    // Bytes a second over the last minute.
    public double VideoRate()
    {
        var since = time.GetUtcNow() - TimeSpan.FromMinutes(1);
        while (_sent.TryPeek(out var oldest) && oldest.At < since)
            _sent.TryDequeue(out _);
        return _sent.Sum(s => s.Bytes) / 60.0;
    }

    private sealed class Closing(Action close) : IDisposable
    {
        public void Dispose() => close();
    }
}
