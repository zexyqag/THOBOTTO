using THOBOTTO.GameServers;
using THOBOTTO.Moderation;
using THOBOTTO.Mischief;
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
        new(ModCommands.ModuleId, "/mod: moderation through the bot"),
    ];

    public static BotModule? Find(string id) => All.FirstOrDefault(m => m.Id == id);
}
