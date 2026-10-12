namespace THOBOTTO.Debugging;

public sealed record DebugEntry(long Id, DateTimeOffset At, LogLevel Level, string Area, string Category, string Message, string? Exception);

// Everything the bot logs, kept in memory while debugging is on (switched on from the panel, off by itself after a
// day): the newest entries, to look through by area, level and text. Nothing is written anywhere.
public sealed class DebugLog(TimeProvider time)
{
    public const int Kept = 5000;
    public static readonly TimeSpan OnFor = TimeSpan.FromHours(24);

    private readonly Queue<DebugEntry> _entries = new();
    private long _lastId;

    public DateTimeOffset? Until { get; private set; }

    public bool On => Until is { } until && time.GetUtcNow() < until;

    public void Start() => Until = time.GetUtcNow() + OnFor;

    public void Stop()
    {
        Until = null;
        lock (_entries)
            _entries.Clear();
    }

    public void Add(LogLevel level, string category, string message, Exception? exception)
    {
        if (!On)
            return;
        var entry = new DebugEntry(Interlocked.Increment(ref _lastId), time.GetUtcNow(), level, AreaOf(category), category, message,
            exception is null ? null : $"{exception.GetType().Name}: {exception.Message}");
        lock (_entries)
        {
            _entries.Enqueue(entry);
            while (_entries.Count > Kept)
                _entries.Dequeue();
        }
    }

    // Newest first.
    public IReadOnlyList<DebugEntry> Entries()
    {
        lock (_entries)
            return [.. _entries.Reverse()];
    }

    public static readonly string[] Areas = ["Music", "Voice", "Videos", "Activity", "Helpers", "Moderation", "Panel", "Discord", "Web server", "Other"];

    // Where a log line comes from, by its logger's namespace.
    public static string AreaOf(string category) => category switch
    {
        _ when category.StartsWith("THOBOTTO.Music") || category.StartsWith("THOBOTTO.Quiz") => "Music",
        _ when category.StartsWith("THOBOTTO.Voice") || category.StartsWith("THOBOTTO.Sounds") => "Voice",
        _ when category.StartsWith("THOBOTTO.Watch") => "Videos",
        _ when category.StartsWith("THOBOTTO.Activity") => "Activity",
        _ when category.StartsWith("THOBOTTO.Helpers") => "Helpers",
        _ when category.StartsWith("THOBOTTO.Moderation") || category.StartsWith("THOBOTTO.Gate") => "Moderation",
        _ when category.StartsWith("THOBOTTO.Panel") || category.StartsWith("THOBOTTO.Integrations") => "Panel",
        _ when category.StartsWith("NetCord") || category.StartsWith("THOBOTTO.Discord") => "Discord",
        _ when category.StartsWith("Microsoft") => "Web server",
        _ => "Other",
    };
}

// Hands every log line to the debug log (which drops them unless debugging is on).
public sealed class DebugLogProvider(DebugLog log) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new Logger(log, categoryName);

    public void Dispose()
    {
    }

    private sealed class Logger(DebugLog log, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => log.On && logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
                log.Add(logLevel, category, formatter(state, exception), exception);
        }
    }
}
