using System.Reflection;
using System.Text.Json;

using THOBOTTO.Access;
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
using THOBOTTO.Modules;
using THOBOTTO.Music;
using THOBOTTO.Points;
using THOBOTTO.Speaking;
using THOBOTTO.Stats;
using THOBOTTO.Voice;

namespace THOBOTTO.Panel;

// A module's settings as a page: what to load, who may change it, and how it's saved (the same
// way as its /setup command, audited).
public sealed record SettingsPage(
    string ModuleId,
    string Title,
    Type Type,
    string Permission,
    Func<ulong, Task<object>> LoadAsync,
    Func<ulong, object, object, ulong, Task> SaveAsync)
{
    public IReadOnlyList<(PropertyInfo Property, SettingAttribute Setting)> Fields { get; } = Type.GetProperties()
        .Select(p => (p, p.GetCustomAttribute<SettingAttribute>()!))
        .Where(f => f.Item2 is not null)
        .ToList();

    // A copy of the settings with some values changed.
    public object With(object current, IReadOnlyDictionary<PropertyInfo, object?> changes)
    {
        var json = JsonSerializer.SerializeToNode(current, Type, JsonSerializerOptions.Web)!.AsObject();
        foreach (var (property, value) in changes)
            json[JsonNamingPolicy.CamelCase.ConvertName(property.Name)] = JsonSerializer.SerializeToNode(value, property.PropertyType, JsonSerializerOptions.Web);
        return json.Deserialize(Type, JsonSerializerOptions.Web)!;
    }
}

public sealed class SettingsPages(SettingsStore settings, PointsEngine points, ServerBoardService board)
{
    public IReadOnlyList<SettingsPage> All { get; } =
    [
        Stored<ModRules>(settings, CaseBook.ModuleId, "Moderation", BotPermissions.ModManage),
        Stored<GateRules>(settings, Gatekeeper.ModuleId, "Gate", BotPermissions.ModManage),
        Stored<EventRules>(settings, EventBoard.ModuleId, "Events", BotPermissions.ManageEvents),
        Stored<GameRules>(settings, GameDirectory.ModuleId, "Games", BotPermissions.ManageGames),
        new(ServerBoardService.ModuleId, "Server board", typeof(ServerSettings), BotPermissions.ManageServers,
            async g => await board.GetSettingsAsync(g),
            (g, _, after, actor) => board.UpdateSettingsAsync(g, actor, "panel", s => CopySettings(after, s))),
        new(PointsEngine.ModuleId, "Points", typeof(PointRules), BotPermissions.ManagePoints,
            g => Task.FromResult<object>(points.Rules(g)),
            (g, before, after, actor) => points.SetRulesAsync(g, (PointRules)after, actor, SettingsStore.Diff((PointRules)before, (PointRules)after))),
        Stored<MischiefRules>(settings, MischiefModule.ModuleId, "Mischief prices", BotPermissions.ManageMischief),
        Stored<FameRules>(settings, HallOfFame.ModuleId, "Hall of fame", BotPermissions.ManageFame),
        Stored<ExpressionRules>(settings, ExpressionShelf.ModuleId, "Emojis and stickers", BotPermissions.ManageExpressions),
        Stored<MusicRules>(settings, MusicService.ModuleId, "Music", BotPermissions.ManageMusic),
        Stored<ArchiveRules>(settings, Archiver.ModuleId, "Archive", BotPermissions.ManageArchive),
        Stored<WrappedRules>(settings, WrappedPoster.ModuleId, "Wrapped", BotPermissions.ManageWrapped),
        Stored<ListeningRules>(settings, VoiceEars.ModuleId, "Voice commands", BotPermissions.ManageMusic),
        Stored<VoiceRules>(settings, DynamicVoice.ModuleId, "Voice channels", BotPermissions.ManageVoiceHubs),
        Stored<SpeechRules>(settings, HelperVoices.ModuleId, "Helper voices", BotPermissions.ManageMusic),
    ];

    public SettingsPage? Find(string moduleId) => All.FirstOrDefault(p => p.ModuleId == moduleId);

    private static SettingsPage Stored<T>(SettingsStore settings, string moduleId, string title, string permission) where T : class, new()
        => new(moduleId, title, typeof(T), permission,
            async g => await settings.GetAsync<T>(g, moduleId),
            (g, before, after, actor) => settings.SetAsync(g, moduleId, (T)after, actor, SettingsStore.Diff((T)before, (T)after)));

    private static void CopySettings(object from, ServerSettings to)
    {
        foreach (var property in typeof(ServerSettings).GetProperties().Where(p => p.GetCustomAttribute<SettingAttribute>() is not null))
            property.SetValue(to, property.GetValue(from));
    }
}
