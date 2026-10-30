using THOBOTTO.Activity.Watch;
using THOBOTTO.Archive;
using THOBOTTO.Events;
using THOBOTTO.Expressions;
using THOBOTTO.Fame;
using THOBOTTO.Games;
using THOBOTTO.GameServers;
using THOBOTTO.Gate;
using THOBOTTO.Happenings;
using THOBOTTO.Mischief;
using THOBOTTO.Moderation;
using THOBOTTO.Music;
using THOBOTTO.Music.Quiz;
using THOBOTTO.Overview;
using THOBOTTO.Points;
using THOBOTTO.Quotes;
using THOBOTTO.Sounds;
using THOBOTTO.Voice.Channels;
using THOBOTTO.Voice.Hearing;
using THOBOTTO.Voice.Speaking;
using THOBOTTO.Wrapped;

namespace THOBOTTO.Modules;

// Where a module sits on the Setup page.
public enum ModuleArea
{
    MusicAndVoice,
    Community,
    Safety,
    Records,
}

// Id is what the commands and the settings keys use; Name is what people see. NeedsMembers: useless without the
// Server Members intent (it must see who joins).
public sealed record BotModule(string Id, string Name, ModuleArea Area, string Description, bool NeedsMembers = false);

public static class ModuleRegistry
{
    // By area, in the order the Setup page lists them.
    public static IReadOnlyList<BotModule> All { get; } =
    [
        new(MusicService.ModuleId, "Music", ModuleArea.MusicAndVoice, "Music in voice channels, played by helper bots"),
        new(WatchRooms.ModuleId, "Watching together", ModuleArea.MusicAndVoice, "Watch videos together in a voice channel's Activity, in step and without ads"),
        new(QuizRooms.ModuleId, "Music quiz", ModuleArea.MusicAndVoice, "A music quiz in a voice channel's Activity: name the song a helper plays"),
        new(VoiceEars.ModuleId, "Voice commands", ModuleArea.MusicAndVoice, "Voice commands: say a helper's name and what to do; the bot only listens to members who opt in"),
        new(HelperVoices.ModuleId, "Helper voices", ModuleArea.MusicAndVoice, "Helpers speak in voice in their personality's voice: replies, questions, now playing, hello and goodbye"),
        new(DynamicVoice.ModuleId, "Voice channels", ModuleArea.MusicAndVoice, "Join a hub voice channel to get a channel of your own"),
        new(EventBoard.ModuleId, "Events", ModuleArea.Community, "Plan events, RSVPs and reminders"),
        new(PointsEngine.ModuleId, "Points", ModuleArea.Community, "Points earned by being active, spent on mischief"),
        new(GameDirectory.ModuleId, "Games", ModuleArea.Community, "Game roles members pick on an overview widget, and sessions linked to game servers"),
        new(HappeningForum.ModuleId, "Happenings forum", ModuleArea.Community, "A forum post for each event, game session, bet and emoji or sound proposal, kept up to date beside its card where it was started, and closed when it's over"),
        new(OverviewBoard.ModuleId, "Overview channels", ModuleArea.Community, "Messages the bot keeps up to date in chosen channels, built of blocks in rows: game servers, the leaderboard, events, game roles, quotes, now playing, the hall of fame, open bets, the most played sounds and notes of your own"),
        new(ServerWatch.ModuleId, "Game servers", ModuleArea.Community, "Game servers and their live status, shown by the overview channels' Game servers block"),
        new(HallOfFame.ModuleId, "Hall of fame", ModuleArea.Community, "Messages many people react to are reposted to a showcase and earn a bonus"),
        new(QuoteCommands.ModuleId, "Quotes", ModuleArea.Community, "The quote book: memorable lines from messages and voice"),
        new(SoundBoard.ModuleId, "Sounds", ModuleArea.Community, "A soundboard of the server's own: any number of sounds, proposed and voted on, played by a helper; join sounds for everyone"),
        new(ExpressionShelf.ModuleId, "Emojis and stickers", ModuleArea.Community, "Members propose emojis and stickers; votes decide, unused ones retire"),
        new(WrappedPoster.ModuleId, "Wrapped", ModuleArea.Community, "Wrapped: music and Discord recaps, on demand, monthly and at the end of the year"),
        new(MischiefModule.ModuleId, "Mischief", ModuleArea.Community, "/rename: anyone renames anyone (never themselves), with a reason"),
        new(CaseBook.ModuleId, "Moderation", ModuleArea.Safety, "/mod: moderation through the bot"),
        new(Gatekeeper.ModuleId, "Gate", ModuleArea.Safety, "The gate: new members verify in a waiting room; raids and too-new accounts are held; a greeting once they're in", NeedsMembers: true),
        new(Archiver.ModuleId, "Archive", ModuleArea.Records, "Keep every message, edit, deletion and reaction, and the server's channels, roles and members, for history and moving platforms"),
    ];

    public static BotModule? Find(string id) => All.FirstOrDefault(m => m.Id == id);

    public static string Name(string id) => Find(id)?.Name ?? id;

    // An area as the Setup page heads it.
    public static string Name(ModuleArea area) => area switch
    {
        ModuleArea.MusicAndVoice => "Music & voice",
        ModuleArea.Community => "Community",
        ModuleArea.Safety => "Safety",
        _ => "Records",
    };

    // What someone is told when what they asked for needs a module that's off in their server.
    public static string Off(string id) => $"This needs the {Name(id)} module, which is off here.";

    // The same, with how to turn it on, for those setting the module up.
    public static string OffAndHowToTurnOn(string id) => $"{Off(id)} Turn it on with `/setup modules enable {id}`.";
}
