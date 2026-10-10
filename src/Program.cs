using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using NetCord;
using NetCord.Gateway;
using NetCord.Hosting.Gateway;
using NetCord.Hosting.Services;
using NetCord.Hosting.Services.ApplicationCommands;
using NetCord.Hosting.Services.ComponentInteractions;
using NetCord.Rest;
using NetCord.Services.ComponentInteractions;

using Npgsql;

using THOBOTTO.Access;
using THOBOTTO.Archive;
using THOBOTTO.Assistant;
using THOBOTTO.Backups;
using THOBOTTO.Data;
using THOBOTTO.Events;
using THOBOTTO.Expressions;
using THOBOTTO.Fame;
using THOBOTTO.GameServers;
using THOBOTTO.Gate;
using THOBOTTO.Games;
using THOBOTTO.Helpers;
using THOBOTTO.Integrations;
using THOBOTTO.Lastfm;
using THOBOTTO.Listening;
using THOBOTTO.Mischief;
using THOBOTTO.Mischief.Bets;
using THOBOTTO.Moderation;
using THOBOTTO.Modules;
using THOBOTTO.Music;
using THOBOTTO.Notifications;
using THOBOTTO.Panel;
using THOBOTTO.Points;
using THOBOTTO.Quotes;
using THOBOTTO.Relay;
using THOBOTTO.Speaking;
using THOBOTTO.Stats;
using THOBOTTO.Voice;

if (args.Contains("--check-voice"))
    Environment.Exit(VoiceCheck.Run());

var builder = WebApplication.CreateBuilder(args);

// Members' games (for naming voice channels) need the presence intent, turned on for the bot in the Developer
// Portal; asking for it without that closes the connection, so it's asked for only when allowed.
var intents = GatewayIntents.AllNonPrivileged | GatewayIntents.MessageContent;
using (var rest = new RestClient(new BotToken(builder.Configuration["Discord:Token"]!)))
{
    var flags = (await rest.GetCurrentApplicationAsync()).Flags ?? default;
    if ((flags & (ApplicationFlags.GatewayPresence | ApplicationFlags.GatewayPresenceLimited)) != 0)
        intents |= GatewayIntents.GuildPresences;
    // Who joins (for the gate), likewise.
    if ((flags & (ApplicationFlags.GatewayGuildUsers | ApplicationFlags.GatewayGuildUsersLimited)) != 0)
        intents |= GatewayIntents.GuildUsers;
}

builder.Services.AddOptions<ArchiveOptions>().BindConfiguration("Archive");
builder.Services.AddOptions<HelpersOptions>().BindConfiguration("Helpers");
builder.Services.AddOptions<LavalinkOptions>().BindConfiguration("Lavalink");

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
    // Started first: others read its settings from the start.
    .AddSingleton<IntegrationStore>()
    .AddHostedService(services => services.GetRequiredService<IntegrationStore>())
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
    .AddSingleton<ChannelNamer>()
    .AddHostedService(services => services.GetRequiredService<ChannelNamer>())
    .AddSingleton<PointsEngine>()
    .AddHostedService(services => services.GetRequiredService<PointsEngine>())
    .AddSingleton<HallOfFame>()
    .AddSingleton<QuoteBook>()
    .AddSingleton(services => IAttachmentStore.Create(services.GetRequiredService<IOptions<ArchiveOptions>>()))
    .AddSingleton<DeletionWitness>()
    .AddSingleton<CaseBook>()
    .AddSingleton<ModActions>()
    .AddSingleton<BanAppeals>()
    .AddSingleton<Gatekeeper>()
    .AddHostedService(services => services.GetRequiredService<Gatekeeper>())
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
    .AddSingleton<ListenTracker>()
    .AddSingleton<PlayHistory>()
    .AddSingleton<GenreBook>()
    .AddSingleton<WrappedStats>()
    .AddSingleton<ActivityStats>()
    .AddSingleton<WrappedService>()
    // Also used without the panel: by backups.
    .AddSingleton<SettingsPages>()
    .AddSingleton<BackupMaker>()
    .AddSingleton<BackupRestorer>()
    .AddSingleton<BackupVault>()
    .AddSingleton<PendingRestores>()
    .AddSingleton<LocalWhisper>()
    .AddSingleton<CloudSpeech>()
    .AddSingleton<SpeechToText>()
    .AddSingleton<ISpeechToText>(services => services.GetRequiredService<SpeechToText>())
    .AddSingleton<ListeningSeats>()
    .AddSingleton<VoicePrefs>()
    .AddHostedService<MusicAutoJoin>()
    .AddSingleton<VoiceRelay>()
    .AddHostedService(services => services.GetRequiredService<VoiceRelay>())
    .AddSingleton<VoiceEars>()
    .AddHostedService(services => services.GetRequiredService<VoiceEars>())
    .AddSingleton<IHelperAware>(services => services.GetRequiredService<VoiceEars>())
    .AddHostedService<VoiceCommands>()
    .AddSingleton<Understanding>()
    .AddSingleton<PeopleFinder>()
    .AddSingleton<VoiceQuestions>()
    .AddSingleton<VoiceTranscript>()
    .AddSingleton<VoiceTrace>()
    .AddSingleton<VoiceMouths>()
    .AddSingleton<PiperVoices>()
    .AddSingleton<SpeechApi>()
    .AddSingleton<HelperVoices>()
    .AddSingleton<HelperSpeech>()
    .AddSingleton<VoiceQuotes>()
    .AddSingleton<IHelperAware>(services => services.GetRequiredService<VoiceQuotes>())
    .AddSingleton<IHelperAware>(services => services.GetRequiredService<VoiceQuestions>())
    .AddSingleton<IVoiceActions, KudosVoiceActions>()
    .AddSingleton<IVoiceActions, MischiefVoiceActions>()
    .AddSingleton<IVoiceActions, PointsVoiceActions>()
    .AddSingleton<IVoiceActions, BetVoiceActions>()
    .AddSingleton<IVoiceActions, EventVoiceActions>()
    .AddHostedService(services => services.GetRequiredService<BackupVault>())
    .AddHostedService<WrappedPoster>()
    .AddSingleton<VoiceLog>()
    .AddHostedService(services => services.GetRequiredService<VoiceLog>())
    .AddSingleton<IHelperAware>(services => services.GetRequiredService<MusicService>())
    .AddSingleton<LavalinkSetup>()
    .AddSingleton<IHelperAware>(services => services.GetRequiredService<LavalinkSetup>())
    .AddSingleton<YoutubeSignIn>()
    .AddSingleton<IntegrationStatus>()
    .AddHostedService(services => services.GetRequiredService<MusicService>())
    .AddSingleton<TimeZones>()
    .AddSingleton<EventBoard>()
    .AddHostedService(services => services.GetRequiredService<EventBoard>())
    .AddSingleton<ExpressionShelf>()
    .AddHostedService(services => services.GetRequiredService<ExpressionShelf>())
    .AddSingleton<BetBook>()
    .AddHostedService(services => services.GetRequiredService<BetBook>())
    .AddSingleton<PaintRoles>()
    .AddSingleton<MischiefActions>()
    .AddHostedService(services => services.GetRequiredService<PaintRoles>())
    .AddDiscordGateway(options => options.Intents = intents)
    .AddApplicationCommands()
    .AddComponentInteractions<ButtonInteraction, ButtonInteractionContext>()
    .AddComponentInteractions<ModalInteraction, ModalInteractionContext>()
    .AddGatewayHandlers(typeof(Program).Assembly);
builder.Services.AddPanel();

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
