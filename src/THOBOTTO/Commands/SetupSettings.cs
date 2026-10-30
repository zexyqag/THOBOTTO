using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;

using NetCord;
using NetCord.Gateway;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;

using THOBOTTO.Access;
using THOBOTTO.Discord;
using THOBOTTO.Modules;
using THOBOTTO.Panel;

namespace THOBOTTO.Commands;

// Any module's settings from Discord, as on the panel: the same fields, checks, permission and saving.
public sealed partial class SetupCommands
{
    [SubSlashCommand("set", "Change one of a module's settings, as on the panel")]
    public async Task<InteractionMessageProperties> SetAsync(
        [SlashCommandParameter(Description = "Module", AutocompleteProviderType = typeof(SettingsModuleAutocomplete))] string module,
        [SlashCommandParameter(Description = "Setting", AutocompleteProviderType = typeof(SettingAutocomplete))] string setting,
        [SlashCommandParameter(Description = "The new value; none clears it. For a list: +x adds, -x takes out, else it's the whole list", MaxLength = 1500, AutocompleteProviderType = typeof(SettingValueAutocomplete))] string value)
    {
        if (await EntryAsync(module) is not { } entry)
            return Replies.Ephemeral(await NoEntryAsync(module));
        if (entry.Fields.FirstOrDefault(f => f.Property.Name == setting) is not { Property: not null } field)
            return Replies.Ephemeral("There's no such setting; pick one from the list.");
        var current = await entry.LoadAsync(GuildId);
        var (posted, problem) = SettingText.Posted(Guild, field.Property, field.Setting, current, value);
        if (problem is not null)
            return Replies.Ephemeral($"{field.Setting.Label}: {problem}");
        var (values, problems) = SettingsForm.Read([field], _ => posted);
        if (problems.Values.FirstOrDefault() is { } invalid)
            return Replies.Ephemeral($"{field.Setting.Label}: {invalid}");
        var after = entry.With(current, values);
        await entry.SaveAsync(GuildId, current, after, Context.User.Id);
        return Replies.Ephemeral($"{entry.Title} · {field.Setting.Label}: {SettingText.Show(field.Property, field.Setting, after)}");
    }

    [SubSlashCommand("show", "A module's settings, as on the panel")]
    public async Task<InteractionMessageProperties> ShowAsync(
        [SlashCommandParameter(Description = "Module", AutocompleteProviderType = typeof(SettingsModuleAutocomplete))] string module)
    {
        if (await EntryAsync(module) is not { } entry)
            return Replies.Ephemeral(await NoEntryAsync(module));
        var current = await entry.LoadAsync(GuildId);
        var lines = entry.Fields.Select(f => $"{(f.Setting.Section is { } section ? $"{section} · " : "")}**{f.Setting.Label}**: {SettingText.Show(f.Property, f.Setting, current)}");
        var text = $"**{entry.Title}**\n{string.Join('\n', lines)}\n-# Change one with `/setup set`.";
        return Replies.Ephemeral(text.Length <= 2000 ? text : text[..1999] + "…");
    }

    // The module's settings, when the member may change them.
    private async Task<SettingsEntry?> EntryAsync(string module)
        => Get<SettingsPages>().Find(module) is { } entry && await Get<AccessControl>().CanAsync(Guild, (GuildUser)Context.User, entry.Permission) ? entry : null;

    private async Task<string> NoEntryAsync(string module)
        => Get<SettingsPages>().Find(module) is { } entry
            ? $"{entry.Title}'s settings need `{entry.Permission}`."
            : "There's no such module with settings; pick one from the list.";
}

// Between a setting and the words for it in Discord.
public static partial class SettingText
{
    // What the panel's form would have posted for the value typed (mentions, names and ids for channels and roles;
    // on or off; a choice by its value or label), or why it can't be.
    public static (IReadOnlyList<string> Posted, string? Problem) Posted(Guild guild, PropertyInfo property, SettingAttribute setting, object current, string value)
    {
        var text = value.Trim();
        var none = text.Equals("none", StringComparison.OrdinalIgnoreCase);
        var type = SettingsForm.Plain(property);
        if (type == typeof(bool))
        {
            return text.ToLowerInvariant() switch
            {
                "on" or "yes" or "true" or "1" => (["true"], null),
                "off" or "no" or "false" or "0" => ([], null),
                _ => ([], "on or off."),
            };
        }
        if (property.PropertyType == typeof(IReadOnlyList<ulong>))
        {
            var ids = SettingsForm.Ids(property, current).ToList();
            var items = Items(text);
            if (none)
                return ([], null);
            var changes = items.All(i => i[0] is '+' or '-');
            if (!changes)
                ids.Clear();
            foreach (var item in items)
            {
                if (Resolve(guild, setting.Kind, item.TrimStart('+', '-')) is not { } id)
                    return ([], $"I can't find {item.TrimStart('+', '-')}.");
                if (item[0] == '-')
                    ids.Remove(id);
                else if (!ids.Contains(id))
                    ids.Add(id);
            }
            return ([.. ids.Select(i => i.ToString(CultureInfo.InvariantCulture))], null);
        }
        if (property.PropertyType == typeof(IReadOnlyList<string>))
        {
            var items = Items(text);
            if (none)
                return ([""], null);
            if (!items.All(i => i[0] is '+' or '-'))
                return ([text], null);
            var list = (property.GetValue(current) as IReadOnlyList<string> ?? []).ToList();
            foreach (var item in items)
            {
                if (item[0] == '-')
                    list.RemoveAll(i => i.Equals(item[1..], StringComparison.OrdinalIgnoreCase));
                else if (!list.Contains(item[1..], StringComparer.OrdinalIgnoreCase))
                    list.Add(item[1..]);
            }
            return ([string.Join(", ", list)], null);
        }
        if (type == typeof(ulong))
            return none ? ([""], null) : Resolve(guild, setting.Kind, text) is { } id ? ([id.ToString(CultureInfo.InvariantCulture)], null) : ([], $"I can't find {text}.");
        if (setting.Choices is { } choices)
            return choices.Select(c => c.Split('=', 2)).FirstOrDefault(c => c[0].Equals(text, StringComparison.OrdinalIgnoreCase) || c[^1].Equals(text, StringComparison.OrdinalIgnoreCase)) is { } choice
                ? ([choice[0]], null)
                : ([], $"one of: {string.Join(", ", choices.Select(c => c.Split('=', 2)[^1]))}.");
        return ([none ? "" : text], null);
    }

    // The value as Discord shows it: channels and roles mentioned, choices by their label.
    public static string Show(PropertyInfo property, SettingAttribute setting, object settings)
    {
        var value = property.GetValue(settings);
        string Id(ulong id) => setting.Kind == SettingKind.Role ? $"<@&{id}>" : $"<#{id}>";
        return value switch
        {
            null => "none",
            bool on => on ? "on" : "off",
            IReadOnlyList<ulong> { Count: 0 } or IReadOnlyList<string> { Count: 0 } => "none",
            IReadOnlyList<ulong> ids => string.Join(", ", ids.Select(Id)),
            ulong id => Id(id),
            string choice when setting.Choices is { } choices => choices.Select(c => c.Split('=', 2)).FirstOrDefault(c => c[0] == choice)?[^1] ?? choice,
            _ => SettingsForm.Text(property, setting, settings) is { Length: > 0 } shown ? $"{shown}{(setting.Unit is { } unit ? $" {unit}" : "")}" : "none",
        };
    }

    // A channel or role by mention, id or name.
    public static ulong? Resolve(Guild guild, SettingKind kind, string text)
    {
        var plain = text.Trim().TrimStart('#', '@');
        if (Mention().Match(text) is { Success: true } mention)
            plain = mention.Groups[1].Value;
        if (kind == SettingKind.Role)
        {
            return ulong.TryParse(plain, out var roleId) && guild.Roles.ContainsKey(roleId) ? roleId
                : guild.Roles.Values.FirstOrDefault(r => r.Name.Equals(plain, StringComparison.OrdinalIgnoreCase))?.Id;
        }
        var channels = guild.Channels.Values.Where(c => kind switch
        {
            SettingKind.VoiceChannel => c is VoiceGuildChannel,
            SettingKind.Category => c is CategoryGuildChannel,
            SettingKind.ForumChannel => c is ForumGuildChannel,
            _ => c is TextGuildChannel,
        }).ToList();
        return ulong.TryParse(plain, out var channelId) && channels.Any(c => c.Id == channelId) ? channelId
            : channels.FirstOrDefault(c => c.Name.Equals(plain, StringComparison.OrdinalIgnoreCase))?.Id;
    }

    private static List<string> Items(string text) => [.. text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    [GeneratedRegex(@"^<[#@]&?(\d+)>$")]
    private static partial Regex Mention();
}

public sealed class SettingsModuleAutocomplete(SettingsPages pages) : IAutocompleteProvider<AutocompleteInteractionContext>
{
    public ValueTask<IEnumerable<ApplicationCommandOptionChoiceProperties>?> GetChoicesAsync(ApplicationCommandInteractionDataOption option, AutocompleteInteractionContext context)
    {
        var input = option.Value ?? "";
        return ValueTask.FromResult<IEnumerable<ApplicationCommandOptionChoiceProperties>?>(pages.All
            .Where(p => p.Title.Contains(input, StringComparison.OrdinalIgnoreCase) || p.ModuleId.Contains(input, StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p.Title)
            .Take(25)
            .Select(p => new ApplicationCommandOptionChoiceProperties(p.Title, p.ModuleId)));
    }
}

public sealed class SettingAutocomplete(SettingsPages pages) : IAutocompleteProvider<AutocompleteInteractionContext>
{
    public ValueTask<IEnumerable<ApplicationCommandOptionChoiceProperties>?> GetChoicesAsync(ApplicationCommandInteractionDataOption option, AutocompleteInteractionContext context)
    {
        var input = option.Value ?? "";
        var module = SettingOptions.Of(context, "module");
        return ValueTask.FromResult<IEnumerable<ApplicationCommandOptionChoiceProperties>?>(pages.Find(module ?? "") is not { } entry ? []
            : entry.Fields
                .Select(f => (f.Property.Name, Label: f.Setting.Section is { } section ? $"{section} · {f.Setting.Label}" : f.Setting.Label))
                .Where(f => f.Label.Contains(input, StringComparison.OrdinalIgnoreCase))
                .Take(25)
                .Select(f => new ApplicationCommandOptionChoiceProperties(f.Label.Length <= 100 ? f.Label : f.Label[..100], f.Name)));
    }
}

// What fits the setting: on or off, its choices, the channels or roles of its kind, else what it is now.
public sealed class SettingValueAutocomplete(SettingsPages pages, GatewayClient gateway) : IAutocompleteProvider<AutocompleteInteractionContext>
{
    public async ValueTask<IEnumerable<ApplicationCommandOptionChoiceProperties>?> GetChoicesAsync(ApplicationCommandInteractionDataOption option, AutocompleteInteractionContext context)
    {
        var input = option.Value ?? "";
        var guildId = context.Interaction.GuildId!.Value;
        if (pages.Find(SettingOptions.Of(context, "module") ?? "") is not { } entry
            || entry.Fields.FirstOrDefault(f => f.Property.Name == SettingOptions.Of(context, "setting")) is not { Property: not null } field
            || !gateway.Cache.Guilds.TryGetValue(guildId, out var guild))
            return [];
        var (property, setting) = field;
        IEnumerable<(string Name, string Value)> choices;
        if (SettingsForm.Plain(property) == typeof(bool))
            choices = [("on", "on"), ("off", "off")];
        else if (setting.Choices is { } fixedChoices)
            choices = fixedChoices.Select(c => c.Split('=', 2)).Select(c => (c[^1], c[0]));
        else if (setting.Kind == SettingKind.Role)
            choices = guild.Roles.Values.Where(r => r.Id != guild.Id && !r.Managed).OrderByDescending(r => r.Position).Select(r => ($"@{r.Name}", r.Id.ToString(CultureInfo.InvariantCulture)));
        else if (setting.Kind is SettingKind.TextChannel or SettingKind.VoiceChannel or SettingKind.Category or SettingKind.ForumChannel)
            choices = guild.Channels.Values.Where(c => SettingText.Resolve(guild, setting.Kind, c.Id.ToString(CultureInfo.InvariantCulture)) is not null)
                .OrderBy(c => c.Position).Select(c => ($"#{c.Name}", c.Id.ToString(CultureInfo.InvariantCulture)));
        else
            choices = [(SettingText.Show(property, setting, await entry.LoadAsync(guildId)), SettingsForm.Text(property, setting, await entry.LoadAsync(guildId)))];
        return choices
            .Where(c => c.Name.Contains(input, StringComparison.OrdinalIgnoreCase) && c.Value.Length is > 0 and <= 100)
            .Take(25)
            .Select(c => new ApplicationCommandOptionChoiceProperties(c.Name.Length <= 100 ? c.Name : c.Name[..100], c.Value));
    }
}

public static class SettingOptions
{
    // Another option's value in the same command, as typed so far.
    public static string? Of(AutocompleteInteractionContext context, string name)
        => Flatten(context.Interaction.Data.Options).FirstOrDefault(o => o.Name == name)?.Value;

    private static IEnumerable<ApplicationCommandInteractionDataOption> Flatten(IEnumerable<ApplicationCommandInteractionDataOption>? options)
        => options?.SelectMany(o => Flatten(o.Options).Prepend(o)) ?? [];
}
