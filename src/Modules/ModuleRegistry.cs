namespace THOBOTTO.Modules;

public sealed record BotModule(string Id, string Description);

public static class ModuleRegistry
{
    public static IReadOnlyList<BotModule> All { get; } = [];

    public static BotModule? Find(string id) => All.FirstOrDefault(m => m.Id == id);
}
