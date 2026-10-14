using THOBOTTO.Archive;
using THOBOTTO.Panel;
using THOBOTTO.Voice.Commands;

namespace THOBOTTO.Sounds;

public static class SoundsStartup
{
    public static IServiceCollection AddSounds(this IServiceCollection services)
    {
        services.AddHttpClient(nameof(SoundBoard), c => c.Timeout = TimeSpan.FromSeconds(30)).AddAsKeyed(ServiceLifetime.Singleton);
        return services
            .AddSingleton<SoundBoard>()
            .AddHostedService(s => s.GetRequiredService<SoundBoard>())
            .AddSingleton<IVoiceActions, SoundVoiceActions>()
            .AddSingleton<IExportSource, SoundsExport>()
            .AddSingleton(new GuildNavEntry("sounds", "Sounds", GuildNavGroup.Community, 50, Module: SoundBoard.ModuleId));
    }

    public static WebApplication MapSounds(this WebApplication app)
    {
        app.MapGet("/g/{guildId}/sounds/{id}/file", (HttpContext context, ulong guildId, long id, PanelAccess access, SoundBoard board)
            => SoundFiles.ServeAsync(context, guildId, id, access, board)).RequireAuthorization();
        return app;
    }
}
