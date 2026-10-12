using THOBOTTO.Activity.Watch;
using THOBOTTO.Archive;
using THOBOTTO.Events;
using THOBOTTO.Expressions;
using THOBOTTO.Fame;
using THOBOTTO.Games;
using THOBOTTO.GameServers;
using THOBOTTO.Gate;
using THOBOTTO.Mischief;
using THOBOTTO.Moderation;
using THOBOTTO.Music;
using THOBOTTO.Music.Quiz;
using THOBOTTO.Points;
using THOBOTTO.Quotes;
using THOBOTTO.Sounds;
using THOBOTTO.Voice.Channels;
using THOBOTTO.Voice.Hearing;
using THOBOTTO.Voice.Speaking;
using THOBOTTO.Wrapped;

namespace THOBOTTO.Modules;

// Id is what the commands and the settings keys use; Name is what people see.
public sealed record BotModule(string Id, string Name, string Description);

public static class ModuleRegistry
{
    public static IReadOnlyList<BotModule> All { get; } =
    [
        new(DynamicVoice.ModuleId, "Voice channels", "Join a hub voice channel to get a channel of your own"),
        new(ServerBoardService.ModuleId, "Game servers", "Live status of game servers on a board channel"),
        new(MischiefModule.ModuleId, "Mischief", "/rename: anyone renames anyone (never themselves), with a reason"),
        new(PointsEngine.ModuleId, "Points", "Points earned by being active, spent on mischief"),
        new(HallOfFame.ModuleId, "Hall of fame", "Messages many people react to are reposted to a showcase and earn a bonus"),
        new(QuoteCommands.ModuleId, "Quotes", "The quote book: memorable lines from messages and voice"),
        new(SoundBoard.ModuleId, "Sounds", "A soundboard of the server's own: any number of sounds, proposed and voted on, played by a helper; join sounds for everyone"),
        new(ExpressionShelf.ModuleId, "Emojis and stickers", "Members propose emojis and stickers; votes decide, unused ones retire"),
        new(Archiver.ModuleId, "Archive", "Keep every message, edit and deletion, for history and moving platforms"),
        new(EventBoard.ModuleId, "Events", "Plan events, RSVPs and reminders"),
        new(GameDirectory.ModuleId, "Games", "Game roles, a role picker, and sessions linked to the game server board"),
        new(MusicService.ModuleId, "Music", "Music in voice channels, played by helper bots"),
        new(WatchRooms.ModuleId, "Watching together", "Watch videos together in a voice channel's Activity, in step and without ads"),
        new(QuizRooms.ModuleId, "Music quiz", "A music quiz in a voice channel's Activity: name the song a helper plays"),
        new(CaseBook.ModuleId, "Moderation", "/mod: moderation through the bot"),
        new(Gatekeeper.ModuleId, "Gate", "The gate: new members verify in a waiting room; raids and too-new accounts are held"),
        new(VoiceEars.ModuleId, "Voice commands", "Voice commands: say a helper's name and what to do; the bot only listens to members who opt in"),
        new(HelperVoices.ModuleId, "Helper voices", "Helpers speak in voice in their personality's voice: replies, questions, now playing, hello and goodbye"),
        new(WrappedPoster.ModuleId, "Wrapped", "Wrapped: music and Discord recaps, on demand, monthly and at the end of the year"),
    ];

    public static BotModule? Find(string id) => All.FirstOrDefault(m => m.Id == id);

    public static string Name(string id) => Find(id)?.Name ?? id;

    // What someone is told when what they asked for needs a module that's off in their server.
    public static string Off(string id) => $"This needs the {Name(id)} module, which is off here.";

    // The same, with how to turn it on, for those setting the module up.
    public static string OffAndHowToTurnOn(string id) => $"{Off(id)} Turn it on with `/setup modules enable {id}`.";
}
