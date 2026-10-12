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

using THOBOTTO;
using THOBOTTO.Access;
using THOBOTTO.Activity;
using THOBOTTO.Archive;
using THOBOTTO.Assistant;
using THOBOTTO.Backups;
using THOBOTTO.Certificates;
using THOBOTTO.Data;
using THOBOTTO.Debugging;
using THOBOTTO.Discord;
using THOBOTTO.Events;
using THOBOTTO.Expressions;
using THOBOTTO.Fame;
using THOBOTTO.Feedback;
using THOBOTTO.Games;
using THOBOTTO.GameServers;
using THOBOTTO.Gate;
using THOBOTTO.Helpers;
using THOBOTTO.Integrations;
using THOBOTTO.Listening;
using THOBOTTO.Mischief;
using THOBOTTO.Mischief.Bets;
using THOBOTTO.Moderation;
using THOBOTTO.Modules;
using THOBOTTO.Music;
using THOBOTTO.Notifications;
using THOBOTTO.Panel;
using THOBOTTO.Panel.Pages;
using THOBOTTO.Points;
using THOBOTTO.Quiz;
using THOBOTTO.Quotes;
using THOBOTTO.Relay;
using THOBOTTO.Sounds;
using THOBOTTO.Speaking;
using THOBOTTO.Stats;
using THOBOTTO.Voice.Channels;
using THOBOTTO.Watch;

if (args.Contains("--check-voice"))
    Environment.Exit(VoiceCheck.Run());
if (args.Contains("--health"))
    Environment.Exit(Health.Probe());

var builder = WebApplication.CreateBuilder(args);

if (StartCheck.Settings(builder.Configuration) is { Count: > 0 } problems)
{
    Console.Error.WriteLine(string.Join(" ", problems));
    Environment.Exit(1);
}

// While debugging is on (the panel's Debug page), every log line is kept in memory too, the bot's own in detail.
var debugLog = new DebugLog(TimeProvider.System);
builder.Logging.AddProvider(new DebugLogProvider(debugLog))
    .AddFilter<DebugLogProvider>(null, LogLevel.Information)
    .AddFilter<DebugLogProvider>("THOBOTTO", LogLevel.Debug)
    .AddFilter<DebugLogProvider>("Microsoft", LogLevel.Warning);

// An export (`export <guild id> <folder>`, run below) never connects, so the application lookup for the intents
// is skipped; the exporter itself asks Discord for today's names and falls back to the archive's.
(ulong Guild, string Folder)? export = args is ["export", var guild, var folder] ? (ulong.Parse(guild), folder) : null;

// Members' games (for naming voice channels) need the presence intent, turned on for the bot in the Developer
// Portal; asking for it without that closes the connection, so it's asked for only when allowed.
var intents = GatewayIntents.AllNonPrivileged | GatewayIntents.MessageContent;
DiscordApp discordApp = new(default);
if (export is null)
{
    using var rest = new RestClient(new BotToken(builder.Configuration["Discord:Token"]!));
    discordApp = new((await rest.GetCurrentApplicationAsync()).Flags ?? default);
}
if (discordApp.Presences)
    intents |= GatewayIntents.GuildPresences;
// Who joins (for the gate), likewise.
if (discordApp.Members)
    intents |= GatewayIntents.GuildUsers;

builder.Services.AddOptions<ArchiveOptions>().BindConfiguration("Archive");
builder.Services.AddOptions<HelpersOptions>().BindConfiguration("Helpers");
builder.Services.AddOptions<LavalinkOptions>().BindConfiguration("Lavalink");
builder.Services.AddOptions<GameDigOptions>().BindConfiguration("GameDig");

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
    .AddSingleton<ISecretStore>(services => services.GetRequiredService<IntegrationStore>())
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
    .AddHostedService<LinkRefresher>()
    .AddSingleton<GameDirectory>()
    .AddSingleton<INotificationTopicSource>(services => services.GetRequiredService<GameDirectory>())
    .AddSingleton<IEventDecorator>(services => services.GetRequiredService<GameDirectory>())
    .AddHostedService(services => services.GetRequiredService<GameDirectory>())
    .AddSingleton<GameSessions>()
    .AddSingleton<HelperFleet>()
    .AddHostedService(services => services.GetRequiredService<HelperFleet>())
    .AddSingleton<ISecretStore>(services => services.GetRequiredService<HelperFleet>())
    .AddSingleton<PersonalityBook>()
    .AddSingleton<IHelperAware>(services => services.GetRequiredService<PersonalityBook>())
    .AddSingleton<MusicService>()
    .AddSingleton<LyricsFinder>()
    .AddSingleton<PlaylistBook>()
    .AddSingleton<LastfmClient>()
    .AddSingleton<Scrobbler>()
    .AddSingleton<ISecretStore>(services => services.GetRequiredService<Scrobbler>())
    .AddSingleton<KeyRing>()
    .AddSingleton<BlendMaker>()
    .AddSingleton<ListenTracker>()
    .AddSingleton<PlayHistory>()
    .AddSingleton<GenreBook>()
    .AddSingleton<WrappedStats>()
    .AddSingleton<ActivityStats>()
    .AddSingleton<WrappedService>()
    .AddSingleton<WrappedNames>()
    .AddSingleton<SettingsPages>()
    .AddSingleton<BackupMaker>()
    .AddSingleton<BackupRestorer>()
    .AddSingleton<BackupVault>()
    .AddSingleton<PendingRestores>()
    .AddSingleton<LocalWhisper>()
    .AddSingleton<ParakeetSpeech>()
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
    .AddSingleton<ActivitySessions>()
    .AddSingleton<ActivityHub>()
    .AddSingleton<YoutubeCookies>()
    .AddHostedService(services => services.GetRequiredService<YoutubeCookies>())
    .AddSingleton<VideoResolver>()
    .AddSingleton<HlsStreams>()
    .AddSingleton<WatchRooms>()
    .AddHostedService(services => services.GetRequiredService<WatchRooms>())
    .AddSingleton<ActivityPolls>()
    .AddHostedService(services => services.GetRequiredService<ActivityPolls>())
    .AddSingleton<QuizRooms>()
    .AddHostedService(services => services.GetRequiredService<QuizRooms>())
    .AddSingleton<DiscordApi>()
    .AddSingleton<BotOwners>()
    .AddSingleton<FeedbackBox>()
    .AddSingleton(discordApp)
    .AddSingleton(debugLog)
    .AddSingleton<Telemetry>()
    .AddSingleton<SoundBoard>()
    .AddHostedService(services => services.GetRequiredService<SoundBoard>())
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

// Encrypts the panel's login cookies, helper tokens and integration secrets. The keys live in the database,
// encrypted with a certificate kept outside it, so a dump alone reads none of what they protect.
var keysDirectory = builder.Configuration["DataProtection:Directory"] is { Length: > 0 } keys ? keys
    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "thobotto", "keys");
builder.Services.AddDataProtection()
    .PersistKeysToDbContext<BotDbContext>()
    .ProtectKeysWithCertificate(KeyCertificate.LoadOrCreate(keysDirectory))
    .SetApplicationName("THOBOTTO");

var host = builder.Build();

await using (var scope = host.Services.CreateAsyncScope())
{
    var database = scope.ServiceProvider.GetRequiredService<BotDbContext>().Database;
    var newer = StartCheck.NewerMigrations(await database.GetAppliedMigrationsAsync(), database.GetMigrations());
    if (newer.Count > 0)
    {
        host.Logger.LogError("The database is newer than this build: {Migrations}; run the image that made it, or a newer one. If that build was abandoned, delete its row from \"__EFMigrationsHistory\" and drop what it added.", string.Join(", ", newer));
        await host.DisposeAsync();
        Environment.Exit(1);
    }
    await database.MigrateAsync();
}

if (export is { } exporting)
{
    await host.Services.GetRequiredService<ArchiveExporter>().ExportAsync(exporting.Guild, exporting.Folder, CancellationToken.None);
    return;
}

await host.Services.GetRequiredService<KeyRing>().ReplacePlaintextKeysAsync();

host.AddModules(typeof(Program).Assembly);
// Discord adds an entry point command once Activities are on; the bot's commands must then include it.
if (discordApp.Activities)
    host.AddEntryPointCommand("launch", "Open THOBOTTO in your voice channel: now playing, lyrics, the queue");
host.MapPanel();
host.MapActivity();
host.MapHealth();

await host.RunAsync();
