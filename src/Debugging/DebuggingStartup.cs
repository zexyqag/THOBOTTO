namespace THOBOTTO.Debugging;

public static class DebuggingStartup
{
    // While debugging is on (the panel's Debug page), every log line is kept in memory too, the bot's own in detail.
    public static ILoggingBuilder AddDebugLog(this ILoggingBuilder logging, DebugLog log)
        => logging.AddProvider(new DebugLogProvider(log))
            .AddFilter<DebugLogProvider>(null, LogLevel.Information)
            .AddFilter<DebugLogProvider>("THOBOTTO", LogLevel.Debug)
            .AddFilter<DebugLogProvider>("Microsoft", LogLevel.Warning);

    public static IServiceCollection AddDebugging(this IServiceCollection services, DebugLog log)
        => services
            .AddSingleton(log)
            .AddSingleton<Telemetry>()
            .AddSingleton<VoiceTrace>()
            // The bot logs what matters about its calls itself; the factory's four lines per request only crowd the log.
            .ConfigureHttpClientDefaults(http => http.RemoveAllLoggers());
}
