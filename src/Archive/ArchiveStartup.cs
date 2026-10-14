using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using THOBOTTO.Access;
using THOBOTTO.Data;
using THOBOTTO.Panel;

namespace THOBOTTO.Archive;

public static class ArchiveStartup
{
    public static IServiceCollection AddArchive(this IServiceCollection services)
    {
        services.AddOptions<ArchiveOptions>().BindConfiguration("Archive");
        // The client's own timeout is off: the archiver gives each download its own.
        services.AddHttpClient(nameof(Archiver), c => c.Timeout = Timeout.InfiniteTimeSpan).AddAsKeyed(ServiceLifetime.Singleton);
        return services
            .AddSingleton(s => IAttachmentStore.Create(s.GetRequiredService<IOptions<ArchiveOptions>>()))
            .AddSingleton<DeletionWitness>()
            .AddSingleton<ReactionLedger>()
            .AddSingleton<Archiver>()
            .AddHostedService(s => s.GetRequiredService<Archiver>())
            .AddHostedService<ReactionReconciler>()
            .AddSingleton<Purger>()
            .AddSingleton<ArchiveExporter>()
            .AddSingleton<PendingPurges>()
            .AddSingleton<Backfiller>()
            .AddHostedService(s => s.GetRequiredService<Backfiller>())
            .AddHostedService<LinkRefresher>()
            .AddSingleton(new GuildNavEntry("archive", "Archive", GuildNavGroup.Records, 10, BotPermissions.ManageArchive));
    }

    public static WebApplication MapArchive(this WebApplication app)
    {
        app.MapGet("/g/{guildId}/archive/file/{id}", (HttpContext context, ulong guildId, ulong id, bool? download, PanelAccess access, IDbContextFactory<BotDbContext> dbFactory, IAttachmentStore store)
            => ArchiveFiles.ServeAsync(context, guildId, id, download == true, access, dbFactory, store)).RequireAuthorization();
        return app;
    }
}
