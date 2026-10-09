using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using NetCord;
using NetCord.Gateway;
using NetCord.Hosting.Gateway;
using NetCord.Hosting.Services;
using NetCord.Hosting.Services.ApplicationCommands;
using NetCord.Hosting.Services.ComponentInteractions;
using NetCord.Services.ComponentInteractions;

using Npgsql;

using THOBOTTO.Access;
using THOBOTTO.Archive;
using THOBOTTO.Data;
using THOBOTTO.Events;
using THOBOTTO.Expressions;
using THOBOTTO.Fame;
using THOBOTTO.GameServers;
using THOBOTTO.Games;
using THOBOTTO.Helpers;
using THOBOTTO.Lastfm;
using THOBOTTO.Mischief;
using THOBOTTO.Mischief.Bets;
using THOBOTTO.Moderation;
using THOBOTTO.Modules;
using THOBOTTO.Music;
using THOBOTTO.Notifications;
using THOBOTTO.Panel;
using THOBOTTO.Points;
using THOBOTTO.Quotes;
using THOBOTTO.Voice;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOptions<ArchiveOptions>().BindConfiguration("Archive");
builder.Services.AddOptions<HelpersOptions>().BindConfiguration("Helpers");
builder.Services.AddOptions<LavalinkOptions>().BindConfiguration("Lavalink");
builder.Services.AddOptions<LastfmOptions>().BindConfiguration("Lastfm");

builder.Services.AddOptions<GameDigOptions>()
    .BindConfiguration("GameDig")
    .ValidateOnStart();

builder.Services
    .AddDbContextFactory<BotDbContext>(options => options
        .UseNpgsql(new NpgsqlConnectionStringBuilder(builder.Configuration.GetConnectionString("Postgres"))
        {
            // The chiseled runtime image has no Kerberos library; without this Npgsql logs its absence.
            GssEncryptionMode = GssEncryptionMode.Disable,
        }.ConnectionString)
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
    .AddSingleton<AutoModSetup>()
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
    .AddSingleton<HelperFleet>()
    .AddHostedService(services => services.GetRequiredService<HelperFleet>())
    .AddSingleton<PersonalityBook>()
    .AddSingleton<IHelperAware>(services => services.GetRequiredService<PersonalityBook>())
    .AddSingleton<MusicService>()
    .AddSingleton<LyricsFinder>()
    .AddSingleton<PlaylistBook>()
    .AddSingleton<LastfmClient>()
    .AddSingleton<Scrobbler>()
    .AddSingleton<BlendMaker>()
    .AddSingleton<IHelperAware>(services => services.GetRequiredService<MusicService>())
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
builder.Services.AddPanel(builder.Configuration, builder.Environment);

// Encrypts the panel's login cookies and helper tokens; the keys live in the database.
builder.Services.AddDataProtection().PersistKeysToDbContext<BotDbContext>().SetApplicationName("THOBOTTO");

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
host.MapPanel();

await host.RunAsync();
