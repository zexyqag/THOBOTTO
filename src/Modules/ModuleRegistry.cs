using THOBOTTO.Expressions;
using THOBOTTO.Fame;
using THOBOTTO.GameServers;
using THOBOTTO.Mischief;
using THOBOTTO.Moderation;
using THOBOTTO.Points;
using THOBOTTO.Quotes;
using THOBOTTO.Voice;

namespace THOBOTTO.Modules;

public sealed record BotModule(string Id, string Description);

public static class ModuleRegistry
{
    public static IReadOnlyList<BotModule> All { get; } =
    [
        new(DynamicVoice.ModuleId, "Join a hub voice channel to get a channel of your own"),
        new(ServerBoardService.ModuleId, "Live status of game servers on a board channel"),
        new(MischiefCommands.ModuleId, "/rename: anyone renames anyone (never themselves), with a reason"),
        new(PointsEngine.ModuleId, "Points earned by being active, spent on mischief"),
        new(HallOfFame.ModuleId, "Messages many people react to are reposted to a showcase and earn a bonus"),
        new(QuoteCommands.ModuleId, "The quote book: memorable lines from messages and voice"),
        new(ExpressionShelf.ModuleId, "Members propose emojis and stickers; votes decide, unused ones retire"),
        new(ModCommands.ModuleId, "/mod: moderation through the bot"),
    ];

    public static BotModule? Find(string id) => All.FirstOrDefault(m => m.Id == id);
}
