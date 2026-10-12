using THOBOTTO.Helpers;
using THOBOTTO.Panel;

namespace THOBOTTO.Voice.Speaking;

public static class SpeakingStartup
{
    public static IServiceCollection AddSpeaking(this IServiceCollection services)
        => services
            .AddSingleton<PiperVoices>()
            .AddSingleton<SpeechClient>()
            .AddSingleton<HelperVoices>()
            .AddSingleton<IVoiceCatalogue>(s => s.GetRequiredService<HelperVoices>())
            .AddSingleton<HelperSpeech>();

    public static WebApplication MapSpeaking(this WebApplication app)
    {
        app.MapGet("/g/{guildId}/helpers/{id}/voice", (HttpContext context, ulong guildId, long id, string? voice, PanelAccess access, PersonalityBook book, HelperVoices voices)
            => VoicePreview.ServeAsync(context, guildId, id, voice, access, book, voices)).RequireAuthorization();
        return app;
    }
}
