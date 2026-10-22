using THOBOTTO.Helpers;
using THOBOTTO.Panel;

namespace THOBOTTO.Voice.Speaking;

public static class SpeakingStartup
{
    public static IServiceCollection AddSpeaking(this IServiceCollection services)
    {
        services.AddHttpClient(nameof(PiperVoices), c => c.Timeout = TimeSpan.FromMinutes(5)).AddAsKeyed(ServiceLifetime.Singleton);
        services.AddHttpClient(nameof(SpeechClient), c => c.Timeout = TimeSpan.FromSeconds(30)).AddAsKeyed(ServiceLifetime.Singleton);
        return services
            .AddSingleton<PiperVoices>()
            .AddSingleton<EspeakVoices>()
            .AddSingleton<SpeechClient>()
            .AddSingleton<HelperVoices>()
            .AddSingleton<IVoiceCatalogue>(s => s.GetRequiredService<HelperVoices>())
            .AddSingleton<SpeechClips>()
            .AddSingleton<HelperSpeech>();
    }

    public static WebApplication MapSpeaking(this WebApplication app)
    {
        app.MapGet("/speech/{id}", (string id, SpeechClips clips) => clips.Get(id) is { } wav ? Results.File(wav, "audio/wav") : Results.NotFound());
        app.MapGet("/g/{guildId}/helpers/{id}/voice", (HttpContext context, ulong guildId, long id, string? voice, PanelAccess access, PersonalityBook book, HelperVoices voices)
            => VoicePreview.ServeAsync(context, guildId, id, voice, access, book, voices)).RequireAuthorization();
        return app;
    }
}
