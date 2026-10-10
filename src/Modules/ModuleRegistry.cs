using THOBOTTO.Archive;
using THOBOTTO.Events;
using THOBOTTO.Expressions;
using THOBOTTO.Fame;
using THOBOTTO.Listening;
using THOBOTTO.GameServers;
using THOBOTTO.Gate;
using THOBOTTO.Games;
using THOBOTTO.Mischief;
using THOBOTTO.Moderation;
using THOBOTTO.Music;
using THOBOTTO.Points;
using THOBOTTO.Quotes;
using THOBOTTO.Speaking;
using THOBOTTO.Stats;
using THOBOTTO.Voice;

namespace THOBOTTO.Modules;

public sealed record BotModule(string Id, string Description);

public static class ModuleRegistry
{
    public static IReadOnlyList<BotModule> All { get; } =
    [
        new(DynamicVoice.ModuleId, "Join a hub voice channel to get a channel of your own"),
        new(ServerBoardService.ModuleId, "Live status of game servers on a board channel"),
        new(MischiefModule.ModuleId, "/rename: anyone renames anyone (never themselves), with a reason"),
        new(PointsEngine.ModuleId, "Points earned by being active, spent on mischief"),
        new(HallOfFame.ModuleId, "Messages many people react to are reposted to a showcase and earn a bonus"),
        new(QuoteCommands.ModuleId, "The quote book: memorable lines from messages and voice"),
        new(ExpressionShelf.ModuleId, "Members propose emojis and stickers; votes decide, unused ones retire"),
        new(Archiver.ModuleId, "Keep every message, edit and deletion, for history and moving platforms"),
        new(EventBoard.ModuleId, "Plan events, RSVPs and reminders"),
        new(GameDirectory.ModuleId, "Game roles, a role picker, and sessions linked to the server board"),
        new(MusicService.ModuleId, "Music in voice channels, played by helper bots"),
        new(CaseBook.ModuleId, "/mod: moderation through the bot"),
        new(Gatekeeper.ModuleId, "The gate: new members verify in a waiting room; raids and too-new accounts are held"),
        new(VoiceEars.ModuleId, "Voice commands: say a helper's name and what to do; the bot only listens to members who opt in"),
        new(HelperVoices.ModuleId, "Helpers speak in voice in their personality's voice: replies, questions, now playing, hello and goodbye"),
        new(WrappedPoster.ModuleId, "Wrapped: music and Discord recaps, on demand, monthly and at the end of the year"),
    ];

    public static BotModule? Find(string id) => All.FirstOrDefault(m => m.Id == id);
}
