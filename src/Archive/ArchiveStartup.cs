using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using THOBOTTO.Data;
using THOBOTTO.Panel;

namespace THOBOTTO.Archive;

public static class ArchiveStartup
{
    public static IServiceCollection AddArchive(this IServiceCollection services)
    {
        services.AddOptions<ArchiveOptions>().BindConfiguration("Archive");
        return services
            .AddSingleton(s => IAttachmentStore.Create(s.GetRequiredService<IOptions<ArchiveOptions>>()))
            .AddSingleton<DeletionWitness>()
            .AddSingleton<Archiver>()
            .AddHostedService(s => s.GetRequiredService<Archiver>())
            .AddSingleton<Purger>()
            .AddSingleton<ArchiveExporter>()
            .AddSingleton<PendingPurges>()
            .AddSingleton<Backfiller>()
            .AddHostedService(s => s.GetRequiredService<Backfiller>())
            .AddHostedService<LinkRefresher>();
    }

    public static WebApplication MapArchive(this WebApplication app)
    {
        app.MapGet("/g/{guildId}/archive/file/{id}", (HttpContext context, ulong guildId, ulong id, bool? download, PanelAccess access, IDbContextFactory<BotDbContext> dbFactory, IAttachmentStore store)
            => ArchiveFiles.ServeAsync(context, guildId, id, download == true, access, dbFactory, store)).RequireAuthorization();
        return app;
    }
}
