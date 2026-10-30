namespace THOBOTTO.Debugging;

// GuildId: the server the line is about, or null for a line about the bot as a whole.
public sealed record DebugEntry(long Id, DateTimeOffset At, LogLevel Level, string Area, string Category, string Message, string? Exception, ulong? GuildId);

// Everything the bot logs, kept in memory while debugging is on (switched on from the panel, off by itself after a
// day): the newest entries, to look through by area, level and text. Nothing is written anywhere.
public sealed class DebugLog(TimeProvider time)
{
    public const int Kept = 5000;
    public static readonly TimeSpan OnFor = TimeSpan.FromHours(24);

    private const string GuildKey = "GuildId";

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

    public void Add(LogLevel level, string category, string message, Exception? exception, ulong? guildId)
    {
        if (!On)
            return;
        var entry = new DebugEntry(Interlocked.Increment(ref _lastId), time.GetUtcNow(), level, AreaOf(category), category, message,
            exception is null ? null : $"{exception.GetType().Name}: {exception.Message}", guildId);
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

    // A line says which server it's about with a {GuildId} in its template, or by being logged inside a scope for
    // that server: `using var _ = logger.BeginScope(DebugLog.Guild(guildId));` at the start of the work about it. A
    // scope is a list of named values with a GuildId, like a message template's own; the line's own value wins.
    public static IReadOnlyList<KeyValuePair<string, object?>> Guild(ulong guildId) => [new(GuildKey, guildId)];

    // The server a log state or scope names, from its GuildId value: an id, or text holding one.
    public static ulong? GuildOf(object? state)
    {
        if (state is not IReadOnlyList<KeyValuePair<string, object?>> values)
            return null;
        foreach (var (key, value) in values)
        {
            if (key == GuildKey)
                return IdOf(value);
        }
        return null;
    }

    private static ulong? IdOf(object? value) => value switch
    {
        ulong id => id,
        long id when id >= 0 => (ulong)id,
        decimal id when id >= 0 && id <= ulong.MaxValue && id == decimal.Truncate(id) => (ulong)id,
        string text when ulong.TryParse(text, out var id) => id,
        _ => null,
    };

    public static readonly string[] Areas = ["Music", "Voice", "Videos", "Activity", "Helpers", "Moderation", "Panel", "Discord", "Web server", "Other"];

    // Where a log line comes from, by its logger's namespace.
    public static string AreaOf(string category) => category switch
    {
        _ when category.StartsWith("THOBOTTO.Music") => "Music",
        _ when category.StartsWith("THOBOTTO.Voice") || category.StartsWith("THOBOTTO.Sounds") => "Voice",
        _ when category.StartsWith("THOBOTTO.Watch") => "Videos",
        _ when category.StartsWith("THOBOTTO.Activity") || category.StartsWith("THOBOTTO.Polls") => "Activity",
        _ when category.StartsWith("THOBOTTO.Helpers") => "Helpers",
        _ when category.StartsWith("THOBOTTO.Moderation") || category.StartsWith("THOBOTTO.Gate") => "Moderation",
        _ when category.StartsWith("THOBOTTO.Panel") || category.StartsWith("THOBOTTO.Integrations") => "Panel",
        _ when category.StartsWith("NetCord") || category.StartsWith("THOBOTTO.Discord") => "Discord",
        _ when category.StartsWith("Microsoft") => "Web server",
        _ => "Other",
    };
}

// Hands every log line to the debug log (which drops them unless debugging is on), with the server it's about:
// the line's own {GuildId}, else the innermost scope naming one.
public sealed class DebugLogProvider(DebugLog log) : ILoggerProvider, ISupportExternalScope
{
    private IExternalScopeProvider? _scopes;

    public ILogger CreateLogger(string categoryName) => new Logger(this, log, categoryName);

    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;

    public void Dispose()
    {
    }

    private sealed class Logger(DebugLogProvider provider, DebugLog log, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => provider._scopes?.Push(state);

        public bool IsEnabled(LogLevel logLevel) => log.On && logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
                log.Add(logLevel, category, formatter(state, exception), exception, DebugLog.GuildOf(state) ?? ScopeGuild());
        }

        private ulong? ScopeGuild()
        {
            ulong? guildId = null;
            // Outermost first, so the innermost scope that names a server is the one kept.
            provider._scopes?.ForEachScope((scope, _) => guildId = DebugLog.GuildOf(scope) ?? guildId, 0);
            return guildId;
        }
    }
}
