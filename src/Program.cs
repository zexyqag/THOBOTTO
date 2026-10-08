using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using NetCord;
using NetCord.Gateway;
using NetCord.Hosting.Gateway;
using NetCord.Hosting.Services;
using NetCord.Hosting.Services.ApplicationCommands;
using NetCord.Hosting.Services.ComponentInteractions;
using NetCord.Services.ComponentInteractions;

using THOBOTTO.Access;
using THOBOTTO.Archive;
using THOBOTTO.Data;
using THOBOTTO.Events;
using THOBOTTO.Expressions;
using THOBOTTO.Fame;
using THOBOTTO.Games;
using THOBOTTO.GameServers;
using THOBOTTO.Mischief;
using THOBOTTO.Mischief.Bets;
using THOBOTTO.Modules;
using THOBOTTO.Moderation;
using THOBOTTO.Music;
using THOBOTTO.Notifications;
using THOBOTTO.Points;
using THOBOTTO.Quotes;
using THOBOTTO.Voice;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddOptions<ArchiveOptions>().BindConfiguration("Archive");
builder.Services.AddOptions<MusicOptions>().BindConfiguration("Music");
builder.Services.AddOptions<LavalinkOptions>().BindConfiguration("Lavalink");

builder.Services.AddOptions<GameDigOptions>()
    .BindConfiguration("GameDig")
    .ValidateOnStart();

builder.Services
    .AddDbContextFactory<BotDbContext>(options => options
        .UseNpgsql(builder.Configuration.GetConnectionString("Postgres"))
        .UseSnakeCaseNamingConvention())
    .AddSingleton(TimeProvider.System)
    .AddSingleton<ModuleState>()
    .AddSingleton<SettingsStore>()
    .AddSingleton<Notifier>()
    .AddSingleton<AccessControl>()
    .AddSingleton<GameDig>()
    .AddSingleton<ServerBoardService>()
    .AddHostedService(services => services.GetRequiredService<ServerBoardService>())
    .AddSingleton<VoicePresence>()
    .AddSingleton<DynamicVoice>()
    .AddHostedService(services => services.GetRequiredService<DynamicVoice>())
    .AddSingleton<PointsEngine>()
    .AddHostedService(services => services.GetRequiredService<PointsEngine>())
    .AddSingleton<HallOfFame>()
    .AddSingleton<QuoteBook>()
    .AddSingleton(services => IAttachmentStore.Create(services.GetRequiredService<IOptions<ArchiveOptions>>()))
    .AddSingleton<DeletionWitness>()
    .AddSingleton<CaseBook>()
    .AddSingleton<ModActions>()
    .AddHostedService<ModTimers>()
    .AddSingleton<Archiver>()
    .AddHostedService(services => services.GetRequiredService<Archiver>())
    .AddSingleton<Purger>()
    .AddSingleton<ArchiveExporter>()
    .AddSingleton<PendingPurges>()
    .AddSingleton<Backfiller>()
    .AddHostedService(services => services.GetRequiredService<Backfiller>())
    .AddSingleton<GameDirectory>()
    .AddSingleton<INotificationTopicSource>(services => services.GetRequiredService<GameDirectory>())
    .AddSingleton<IEventDecorator>(services => services.GetRequiredService<GameDirectory>())
    .AddHostedService(services => services.GetRequiredService<GameDirectory>())
    .AddSingleton<GameSessions>()
    .AddSingleton<Personalities>()
    .AddSingleton<MusicService>()
    .AddHostedService(services => services.GetRequiredService<MusicService>())
    .AddSingleton<TimeZones>()
    .AddSingleton<EventBoard>()
    .AddHostedService(services => services.GetRequiredService<EventBoard>())
    .AddSingleton<ExpressionShelf>()
    .AddHostedService(services => services.GetRequiredService<ExpressionShelf>())
    .AddSingleton<BetBook>()
    .AddHostedService(services => services.GetRequiredService<BetBook>())
    .AddSingleton<PaintRoles>()
    .AddHostedService(services => services.GetRequiredService<PaintRoles>())
    .AddDiscordGateway(options => options.Intents = GatewayIntents.AllNonPrivileged | GatewayIntents.MessageContent)
    .AddApplicationCommands()
    .AddComponentInteractions<ButtonInteraction, ButtonInteractionContext>()
    .AddComponentInteractions<ModalInteraction, ModalInteractionContext>()
    .AddGatewayHandlers(typeof(Program).Assembly);

var host = builder.Build();

await using (var scope = host.Services.CreateAsyncScope())
    await scope.ServiceProvider.GetRequiredService<BotDbContext>().Database.MigrateAsync();

// `export <guild id> <folder>`: write the archive and exit, without connecting to the gateway.
if (args is ["export", var guild, var folder])
{
    await host.Services.GetRequiredService<ArchiveExporter>().ExportAsync(ulong.Parse(guild), folder, CancellationToken.None);
    return;
}

host.AddModules(typeof(Program).Assembly);

await host.RunAsync();
