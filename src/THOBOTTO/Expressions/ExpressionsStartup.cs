using THOBOTTO.Archive;
using THOBOTTO.Happenings;
using THOBOTTO.Modules;
using THOBOTTO.Panel;

namespace THOBOTTO.Expressions;

public static class ExpressionsStartup
{
    public static IServiceCollection AddExpressions(this IServiceCollection services)
    {
        services.AddHttpClient(nameof(ExpressionShelf)).AddAsKeyed(ServiceLifetime.Singleton);
        return services
            .AddSingleton<ExpressionShelf>()
            .AddSingleton(ExpressionShelf.Happening)
            .AddHostedService(s => s.GetRequiredService<ExpressionShelf>())
            .AddSingleton<IExportSource, ExpressionsExport>()
            .AddSingleton<IPriceSource, ExpressionPrices>()
            .AddSingleton(new GuildNavEntry("emoji", "Emoji", GuildNavGroup.Community, 40, Module: ExpressionShelf.ModuleId));
    }

    public static WebApplication MapExpressions(this WebApplication app)
    {
        app.MapGet("/g/{guildId}/emoji/{id}/image", (HttpContext context, ulong guildId, long id, PanelAccess access, ExpressionShelf shelf)
            => ExpressionImages.ServeAsync(context, guildId, id, access, shelf)).RequireAuthorization();
        return app;
    }
}
