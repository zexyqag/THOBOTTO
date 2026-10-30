using System.Reflection;
using System.Text.Json;

using THOBOTTO.Access;
using THOBOTTO.Archive;
using THOBOTTO.Events;
using THOBOTTO.Expressions;
using THOBOTTO.Fame;
using THOBOTTO.Feeds;
using THOBOTTO.Games;
using THOBOTTO.GameServers;
using THOBOTTO.Gate;
using THOBOTTO.Happenings;
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
using THOBOTTO.Watch;
using THOBOTTO.Wrapped;

namespace THOBOTTO.Modules;

// A module's settings as a page: what to load, who may change it, and how it's saved (the same
// way from /setup set, audited). The page is titled as the module is named.
public sealed record SettingsEntry(
    string ModuleId,
    Type Type,
    string Permission,
    Func<ulong, Task<object>> LoadAsync,
    Func<ulong, object, object, ulong, Task> SaveAsync)
{
    public string Title => ModuleRegistry.Name(ModuleId);

    // For each field of switches, by property name: its choices, as value and label.
    public IReadOnlyDictionary<string, IReadOnlyList<(string Value, string Label)>> Switches { get; init; } = new Dictionary<string, IReadOnlyList<(string, string)>>();

    public IReadOnlyList<(string Value, string Label)> SwitchesOf(PropertyInfo property) => Switches.GetValueOrDefault(property.Name, []);

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

public sealed class SettingsPages(SettingsStore settings, PointsEngine points, ServerWatch watch, IEnumerable<HappeningKind> kinds)
{
    public IReadOnlyList<SettingsEntry> All { get; } =
    [
        Stored<ModRules>(settings, CaseBook.ModuleId, BotPermissions.ModManage),
        Stored<GateRules>(settings, Gatekeeper.ModuleId, BotPermissions.ModManage),
        Stored<EventRules>(settings, EventBoard.ModuleId, BotPermissions.ManageEvents),
        Stored<GameRules>(settings, GameDirectory.ModuleId, BotPermissions.ManageGames),
        Stored<HappeningRules>(settings, HappeningForum.ModuleId, BotPermissions.ManageOverview) with
        {
            Switches = new Dictionary<string, IReadOnlyList<(string, string)>> { [nameof(HappeningRules.Off)] = [.. kinds.OrderBy(k => k.Label).Select(k => (k.Id, k.Label))] },
        },
        new(ServerWatch.ModuleId, typeof(ServerSettings), BotPermissions.ManageGameServers,
            async g => await watch.GetSettingsAsync(g),
            (g, _, after, actor) => watch.UpdateSettingsAsync(g, actor, "panel", s => CopySettings(after, s))),
        new(PointsEngine.ModuleId, typeof(PointRules), BotPermissions.ManagePoints,
            g => Task.FromResult<object>(points.Rules(g)),
            (g, before, after, actor) => points.SetRulesAsync(g, (PointRules)after, actor, SettingsStore.Diff((PointRules)before, (PointRules)after))),
        Stored<MischiefRules>(settings, MischiefModule.ModuleId, BotPermissions.ManageMischief),
        Stored<QuoteRules>(settings, QuoteCommands.ModuleId, BotPermissions.ManageQuotes),
        Stored<FeedRules>(settings, FeedWatch.ModuleId, BotPermissions.ManageFeeds),
        Stored<FameRules>(settings, HallOfFame.ModuleId, BotPermissions.ManageFame),
        Stored<ExpressionRules>(settings, ExpressionShelf.ModuleId, BotPermissions.ManageExpressions),
        Stored<SoundRules>(settings, SoundBoard.ModuleId, BotPermissions.ManageExpressions),
        Stored<MusicRules>(settings, MusicService.ModuleId, BotPermissions.ManageMusic),
        Stored<WatchRules>(settings, WatchRooms.ModuleId, BotPermissions.ManageMusic),
        Stored<QuizRules>(settings, QuizRooms.ModuleId, BotPermissions.ManageMusic),
        Stored<ArchiveRules>(settings, Archiver.ModuleId, BotPermissions.ManageArchive),
        Stored<WrappedRules>(settings, WrappedPoster.ModuleId, BotPermissions.ManageWrapped),
        Stored<ListeningRules>(settings, VoiceEars.ModuleId, BotPermissions.ManageMusic),
        Stored<VoiceRules>(settings, DynamicVoice.ModuleId, BotPermissions.ManageVoiceChannels),
        Stored<SpeechRules>(settings, HelperVoices.ModuleId, BotPermissions.ManageMusic),
    ];

    public SettingsEntry? Find(string moduleId) => All.FirstOrDefault(p => p.ModuleId == moduleId);

    private static SettingsEntry Stored<T>(SettingsStore settings, string moduleId, string permission) where T : class, new()
        => new(moduleId, typeof(T), permission,
            async g => await settings.GetAsync<T>(g, moduleId),
            (g, before, after, actor) => settings.SetAsync(g, moduleId, (T)after, actor, SettingsStore.Diff((T)before, (T)after)));

    private static void CopySettings(object from, ServerSettings to)
    {
        foreach (var property in typeof(ServerSettings).GetProperties().Where(p => p.GetCustomAttribute<SettingAttribute>() is not null))
            property.SetValue(to, property.GetValue(from));
    }
}
