using NetCord;
using NetCord.Gateway;
using NetCord.Hosting.Gateway;
using NetCord.Hosting.Services;
using NetCord.Hosting.Services.ApplicationCommands;
using NetCord.Hosting.Services.ComponentInteractions;
using NetCord.Services.ComponentInteractions;

using THOBOTTO;
using THOBOTTO.Access;
using THOBOTTO.Activity;
using THOBOTTO.Activity.Watch;
using THOBOTTO.Archive;
using THOBOTTO.Backups;
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
using THOBOTTO.Members;
using THOBOTTO.Mischief;
using THOBOTTO.Mischief.Bets;
using THOBOTTO.Moderation;
using THOBOTTO.Modules;
using THOBOTTO.Music;
using THOBOTTO.Music.Quiz;
using THOBOTTO.Notifications;
using THOBOTTO.Panel;
using THOBOTTO.Panel.Pages;
using THOBOTTO.Points;
using THOBOTTO.Quotes;
using THOBOTTO.Sounds;
using THOBOTTO.Voice.Channels;
using THOBOTTO.Voice.Commands;
using THOBOTTO.Voice.Hearing;
using THOBOTTO.Voice.Relay;
using THOBOTTO.Voice.Speaking;
using THOBOTTO.Wrapped;

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

var debugLog = new DebugLog(TimeProvider.System);
builder.Logging.AddDebugLog(debugLog);

// An export (`export <guild id> <folder>`, run below) never connects, so the application lookup for the intents
// is skipped; the exporter itself asks Discord for today's names and falls back to the archive's.
(ulong Guild, string Folder)? export = args is ["export", var guild, var folder] ? (ulong.Parse(guild), folder) : null;

// Members' games (for naming voice channels) need the presence intent, turned on for the bot in the Developer
// Portal; asking for it without that closes the connection, so it's asked for only when allowed. Who joins (for
// the gate), likewise.
var discordApp = export is null ? await DiscordStartup.LookupAsync(builder.Configuration["Discord:Token"]!) : new DiscordApp(default);
var intents = GatewayIntents.AllNonPrivileged | GatewayIntents.MessageContent;
if (discordApp.Presences)
    intents |= GatewayIntents.GuildPresences;
if (discordApp.Members)
    intents |= GatewayIntents.GuildUsers;

builder.Services
    .AddData(builder.Configuration)
    .AddDebugging(debugLog)
    .AddDiscord(discordApp)
    .AddIntegrations()
    .AddModules()
    .AddNotifications()
    .AddAccess()
    .AddMembers()
    .AddHelpers()
    .AddVoiceRelay()
    .AddPanel()
    .AddPages()
    .AddGameServers()
    .AddVoiceChannels()
    .AddFame()
    .AddArchive()
    .AddModeration()
    .AddGate()
    .AddGames()
    .AddSpeaking()
    .AddHearing()
    .AddVoiceCommands()
    // From here, the features with voice actions in the order the language model reads them.
    .AddMusic()
    .AddQuiz()
    .AddQuotes()
    .AddSounds()
    .AddPoints()
    .AddExpressions()
    .AddMischief()
    .AddBets()
    .AddEvents()
    .AddWrapped()
    .AddBackups()
    .AddFeedback()
    .AddWatch()
    .AddActivity()
    // Last: everything above has started when the gateway connects.
    .AddDiscordGateway(options => options.Intents = intents)
    .AddApplicationCommands()
    .AddComponentInteractions<ButtonInteraction, ButtonInteractionContext>()
    .AddComponentInteractions<ModalInteraction, ModalInteractionContext>()
    .AddGatewayHandlers(typeof(Program).Assembly);

var host = builder.Build();
await host.MigrateOrExitAsync();

if (export is { } exporting)
{
    await host.Services.GetRequiredService<ArchiveExporter>().ExportAsync(exporting.Guild, exporting.Folder, CancellationToken.None);
    return;
}

host.AddModules(typeof(Program).Assembly);
// Discord adds an entry point command once Activities are on; the bot's commands must then include it.
if (discordApp.Activities)
    host.AddEntryPointCommand("launch", "Open THOBOTTO in your voice channel: now playing, lyrics, the queue");
host.MapPanel()
    .MapHelpers()
    .MapArchive()
    .MapSounds()
    .MapSpeaking()
    .MapMusic()
    .MapBackups()
    .MapActivity();
host.MapHealth();

await host.RunAsync();
