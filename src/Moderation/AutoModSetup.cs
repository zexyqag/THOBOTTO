using System.Collections.Concurrent;

using NetCord;
using NetCord.Gateway;
using NetCord.Hosting.Gateway;
using NetCord.Rest;

using THOBOTTO.Modules;

namespace THOBOTTO.Moderation;

public enum AutoModFilter
{
    Words,
    Links,
    Invites,
    Spam,
    Mentions,
    Profanity,
    [NetCord.Services.ApplicationCommands.SlashCommandChoice(Name = "Sexual content")]
    SexualContent,
    Slurs,
}

// Sets up Discord's own AutoMod, which blocks a message before it's posted. The bot keeps its own
// rules, named "THOBOTTO: …", and leaves others alone; what they block becomes a case.
public sealed class AutoModSetup(RestClient rest, SettingsStore settings)
{
    private const string Prefix = "THOBOTTO: ";
    private const string ListsRule = Prefix + "Word lists";
    private const string Blocked = "Blocked by this server's filter.";

    public async Task<IReadOnlyList<AutoModerationRule>> RulesAsync(ulong guildId)
        => (await rest.GetAutoModerationRulesAsync(guildId)).ToList();

    // Turns a filter on (creating its rule) or off. Words need words first; see WordsAsync.
    public async Task<string> SetAsync(ulong guildId, AutoModFilter filter, bool on, TimeSpan? timeout, int? mentionLimit)
    {
        var rules = await RulesAsync(guildId);
        if (filter is AutoModFilter.Profanity or AutoModFilter.SexualContent or AutoModFilter.Slurs)
            return await SetListAsync(guildId, rules, Preset(filter), on);

        var name = Prefix + filter;
        var existing = rules.FirstOrDefault(r => r.Name == name);
        if (!on)
        {
            if (existing is not null)
                await rest.ModifyAutoModerationRuleAsync(guildId, existing.Id, o => o.Enabled = false);
            return $"{filter} filter is off.";
        }
        if (filter == AutoModFilter.Words && (existing?.TriggerMetadata.KeywordFilter?.Count ?? 0) == 0)
            return "Add words first: `/setup automod words change:Add`.";

        var trigger = existing?.TriggerMetadata is { } m ? Copy(m) : Trigger(filter, mentionLimit);
        if (filter == AutoModFilter.Mentions && mentionLimit is { } limit)
            trigger.MentionTotalLimit = limit;
        var actions = Actions(filter, timeout);
        var exempt = await ExemptAsync(guildId);
        if (existing is null)
            await rest.CreateAutoModerationRuleAsync(guildId, new(name, AutoModerationRuleEventType.MessageSend, TriggerType(filter), actions)
            {
                TriggerMetadata = trigger,
                Enabled = true,
                ExemptRoles = exempt,
            });
        else
            await rest.ModifyAutoModerationRuleAsync(guildId, existing.Id, o =>
            {
                o.TriggerMetadata = trigger;
                o.Actions = actions;
                o.Enabled = true;
                o.ExemptRoles = exempt;
            });
        return $"{filter} filter is on{(timeout is { } t && CanTimeout(filter) ? $", and times senders out for {Durations.Format(t)}" : "")}.";
    }

    // Adds or removes blocked words (Discord's syntax: *word* matches inside other words).
    public async Task<string> WordsAsync(ulong guildId, IReadOnlyList<string> words, bool add)
    {
        var rules = await RulesAsync(guildId);
        var existing = rules.FirstOrDefault(r => r.Name == Prefix + AutoModFilter.Words);
        var current = existing?.TriggerMetadata.KeywordFilter?.ToList() ?? [];
        var updated = add ? current.Union(words, StringComparer.OrdinalIgnoreCase).ToList() : current.Where(w => !words.Contains(w, StringComparer.OrdinalIgnoreCase)).ToList();
        if (updated.Count > 1000)
            return "Discord allows up to 1000 words in a filter.";

        var trigger = existing?.TriggerMetadata is { } m ? Copy(m) : new();
        trigger.KeywordFilter = updated;
        if (existing is null)
        {
            if (!add)
                return "There are no blocked words.";
            await rest.CreateAutoModerationRuleAsync(guildId, new(Prefix + AutoModFilter.Words, AutoModerationRuleEventType.MessageSend, AutoModerationRuleTriggerType.Keyword, Actions(AutoModFilter.Words, null))
            {
                TriggerMetadata = trigger,
                Enabled = true,
                ExemptRoles = await ExemptAsync(guildId),
            });
        }
        else
            await rest.ModifyAutoModerationRuleAsync(guildId, existing.Id, o => o.TriggerMetadata = trigger);
        return $"{updated.Count} blocked word{(updated.Count == 1 ? "" : "s")}: {(updated.Count == 0 ? "none" : string.Join(", ", updated.Select(w => $"`{w}`")))}";
    }

    // Sites that may be linked when the links filter is on, e.g. youtube.com.
    public async Task<string> AllowLinksAsync(ulong guildId, IReadOnlyList<string> sites, bool add)
    {
        var rule = (await RulesAsync(guildId)).FirstOrDefault(r => r.Name == Prefix + AutoModFilter.Links);
        if (rule is null)
            return "Turn the links filter on first: `/setup automod filter filter:Links on:True`.";
        var patterns = sites.Select(s => $"*{s.Trim('*')}*").ToList();
        var current = rule.TriggerMetadata.AllowList?.ToList() ?? [];
        var updated = add ? current.Union(patterns, StringComparer.OrdinalIgnoreCase).ToList() : current.Where(a => !patterns.Contains(a, StringComparer.OrdinalIgnoreCase)).ToList();
        var trigger = Copy(rule.TriggerMetadata);
        trigger.AllowList = updated;
        await rest.ModifyAutoModerationRuleAsync(guildId, rule.Id, o => o.TriggerMetadata = trigger);
        return $"Links allowed: {(updated.Count == 0 ? "none" : string.Join(", ", updated.Select(a => $"`{a.Trim('*')}`")))}";
    }

    // Roles the bot's filters skip, e.g. moderators. Applied to every one of its rules.
    public async Task<string> ExemptRoleAsync(ulong guildId, ulong roleId, bool exempt, ulong actorId)
    {
        var before = await settings.GetAsync<ModRules>(guildId, CaseBook.ModuleId);
        var roles = before.AutoModExemptRoleIds.Where(r => r != roleId).ToList();
        if (exempt)
            roles.Add(roleId);
        await settings.SetAsync(guildId, CaseBook.ModuleId, before with { AutoModExemptRoleIds = roles }, actorId, $"automod exempt {roleId}: {exempt}");

        foreach (var rule in (await RulesAsync(guildId)).Where(r => r.Name.StartsWith(Prefix)))
            await rest.ModifyAutoModerationRuleAsync(guildId, rule.Id, o => o.ExemptRoles = roles);
        return $"The filters skip: {(roles.Count == 0 ? "nobody (people who can manage the server always are)" : string.Join(", ", roles.Select(r => $"<@&{r}>")))}";
    }

    private async Task<string> SetListAsync(ulong guildId, IReadOnlyList<AutoModerationRule> rules, AutoModerationRuleKeywordPresetType preset, bool on)
    {
        var existing = rules.FirstOrDefault(r => r.Name == ListsRule);
        var presets = existing?.TriggerMetadata.Presets?.ToList() ?? [];
        presets.Remove(preset);
        if (on)
            presets.Add(preset);
        var trigger = existing?.TriggerMetadata is { } m ? Copy(m) : new();
        trigger.Presets = presets;

        if (existing is null)
        {
            if (!on)
                return "That word list is off.";
            await rest.CreateAutoModerationRuleAsync(guildId, new(ListsRule, AutoModerationRuleEventType.MessageSend, AutoModerationRuleTriggerType.KeywordPreset, Actions(AutoModFilter.Profanity, null))
            {
                TriggerMetadata = trigger,
                Enabled = true,
                ExemptRoles = await ExemptAsync(guildId),
            });
        }
        else
            await rest.ModifyAutoModerationRuleAsync(guildId, existing.Id, o =>
            {
                o.TriggerMetadata = trigger;
                o.Enabled = presets.Count > 0;
            });
        return $"Discord's word lists on: {(presets.Count == 0 ? "none" : string.Join(", ", presets))}.";
    }

    private async Task<IEnumerable<ulong>> ExemptAsync(ulong guildId)
        => (await settings.GetAsync<ModRules>(guildId, CaseBook.ModuleId)).AutoModExemptRoleIds;

    private static AutoModerationRuleTriggerType TriggerType(AutoModFilter filter) => filter switch
    {
        AutoModFilter.Spam => AutoModerationRuleTriggerType.Spam,
        AutoModFilter.Mentions => AutoModerationRuleTriggerType.MentionSpam,
        _ => AutoModerationRuleTriggerType.Keyword,
    };

    private static AutoModerationRuleTriggerMetadataProperties Trigger(AutoModFilter filter, int? mentionLimit) => filter switch
    {
        AutoModFilter.Links => new() { RegexPatterns = [@"https?://\S+"] },
        AutoModFilter.Invites => new() { RegexPatterns = [@"discord(?:\.gg|(?:app)?\.com/invite)/\S+"] },
        AutoModFilter.Mentions => new() { MentionTotalLimit = mentionLimit ?? 5, MentionRaidProtectionEnabled = true },
        _ => new(),
    };

    private static AutoModerationRuleTriggerMetadataProperties Copy(AutoModerationRuleTriggerMetadata m) => new()
    {
        KeywordFilter = m.KeywordFilter,
        RegexPatterns = m.RegexPatterns,
        Presets = m.Presets,
        AllowList = m.AllowList,
        MentionTotalLimit = m.MentionTotalLimit,
        MentionRaidProtectionEnabled = m.MentionRaidProtectionEnabled,
    };

    // Discord only lets keyword and mention rules time people out.
    private static bool CanTimeout(AutoModFilter filter) => filter is AutoModFilter.Words or AutoModFilter.Links or AutoModFilter.Invites or AutoModFilter.Mentions;

    private static List<AutoModerationActionProperties> Actions(AutoModFilter filter, TimeSpan? timeout)
    {
        var actions = new List<AutoModerationActionProperties>
        {
            new(AutoModerationActionType.BlockMessage) { Metadata = new() { CustomMessage = Blocked } },
        };
        if (timeout is { } t && CanTimeout(filter))
            actions.Add(new(AutoModerationActionType.Timeout) { Metadata = new() { DurationSeconds = (int)t.TotalSeconds } });
        return actions;
    }

    private static AutoModerationRuleKeywordPresetType Preset(AutoModFilter filter) => filter switch
    {
        AutoModFilter.Profanity => AutoModerationRuleKeywordPresetType.Profanity,
        AutoModFilter.SexualContent => AutoModerationRuleKeywordPresetType.SexualContent,
        _ => AutoModerationRuleKeywordPresetType.Slurs,
    };
}

// What AutoMod did becomes a case: a blocked message (with its text) or a timeout.
public sealed class AutoModCases(CaseBook cases, ModuleState modules, RestClient rest, TimeProvider time) : IAutoModerationActionExecutionGatewayHandler
{
    // Rule id → name, to say which filter it was.
    private readonly ConcurrentDictionary<ulong, string> _ruleNames = new();

    public async ValueTask HandleAsync(AutoModerationActionExecutionEventArgs e)
    {
        if (e.Action.Type is not (AutoModerationActionType.BlockMessage or AutoModerationActionType.Timeout)
            || !await modules.IsEnabledAsync(e.GuildId, CaseBook.ModuleId))
            return;

        if (!_ruleNames.TryGetValue(e.RuleId, out var rule))
        {
            try
            {
                rule = _ruleNames[e.RuleId] = (await rest.GetAutoModerationRuleAsync(e.GuildId, e.RuleId)).Name;
            }
            catch (RestException)
            {
                rule = "AutoMod";
            }
        }

        var now = time.GetUtcNow();
        var reason = $"{rule}{(string.IsNullOrEmpty(e.MatchedKeyword) ? "" : $": matched `{e.MatchedKeyword}`")}";
        var c = new ModCase
        {
            GuildId = e.GuildId,
            Type = e.Action.Type == AutoModerationActionType.Timeout ? CaseTypes.Timeout : CaseTypes.AutoMod,
            TargetId = e.UserId,
            ModeratorId = CaseTypes.AutoModId,
            Reason = reason,
            ChannelId = e.ChannelId,
            CreatedAt = now,
            Details = string.IsNullOrEmpty(e.Content) ? null : e.Content,
            EndsAt = e.Action.Metadata?.DurationSeconds is { } seconds ? now + TimeSpan.FromSeconds(seconds) : null,
        };
        await cases.OpenAsync(c, dm: false);
    }
}
