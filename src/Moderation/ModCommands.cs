using System.Net;

using Microsoft.EntityFrameworkCore;

using NetCord;
using NetCord.Gateway;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;

using THOBOTTO.Access;
using THOBOTTO.Data;
using THOBOTTO.Modules;

namespace THOBOTTO.Moderation;

[SlashCommand("mod", "Moderation", Contexts = [InteractionContextType.Guild])]
public sealed class ModCommands(ModuleState modules, CaseBook cases, ModActions actions, SettingsStore settings, IDbContextFactory<BotDbContext> dbFactory, TimeProvider time)
    : ApplicationCommandModule<ApplicationCommandContext>
{
    public const string ModuleId = CaseBook.ModuleId;

    private Guild Guild => Context.Guild!;

    private GuildUser Actor => (GuildUser)Context.User;

    [SubSlashCommand("warn", "Warn a member; they're told why")]
    [RequirePermission(BotPermissions.ModWarn)]
    public async Task WarnAsync(
        [SlashCommandParameter(Description = "Member")] GuildUser member,
        [SlashCommandParameter(Description = "Why; the member sees this", MaxLength = 500)] string reason)
        => await RunAsync(await RefusalAsync(member), () => actions.WarnAsync(Me, member.Id, reason));

    [SubSlashCommand("escalate", "What happens on its own as warnings add up (needs mod.manage)")]
    [RequirePermission(BotPermissions.ModManage)]
    public async Task<InteractionMessageProperties> EscalateAsync(
        [SlashCommandParameter(Description = "At this many active warnings", MinValue = 1, MaxValue = 50)] int warnings,
        [SlashCommandParameter(Description = "What the bot does then")] EscalationChoice action,
        [SlashCommandParameter(Description = "For how long, e.g. 1h or 7d (timeouts default to 1h; bans without it are for good)", MaxLength = 20)] string? duration = null)
    {
        var length = duration is null ? null : Durations.Parse(duration);
        if (duration is not null && length is null)
            return Replies.Ephemeral("Durations look like 1h, 2d or 1w.");
        if (action == EscalationChoice.Timeout && length > ModActions.MaxTimeout)
            return Replies.Ephemeral("Discord allows timeouts of up to 28 days.");

        var before = await settings.GetAsync<ModRules>(Guild.Id, ModuleId);
        var steps = before.Escalations.Where(s => s.Warnings != warnings).ToList();
        if (action != EscalationChoice.Nothing)
            steps.Add(new(warnings, action.ToString().ToLowerInvariant(), action == EscalationChoice.Kick ? null : (int?)length?.TotalMinutes));
        var after = before with { Escalations = steps.OrderBy(s => s.Warnings).ToList() };
        await settings.SetAsync(Guild.Id, ModuleId, after, Actor.Id, $"escalation at {warnings}: {action} {duration}");

        return Replies.Ephemeral(after.Escalations.Count == 0
            ? "Warnings don't lead to anything on their own."
            : $"As warnings add up (counting {after.WarningDays} days):\n" + string.Join('\n', after.Escalations.Select(s => "• " + Escalation.Describe(s))));
    }

    [SubSlashCommand("timeout", "Time a member out: they can't talk, react or join voice")]
    [RequirePermission(BotPermissions.ModTimeout)]
    public async Task TimeoutAsync(
        [SlashCommandParameter(Description = "Member")] GuildUser member,
        [SlashCommandParameter(Description = "How long: 10m, 1h, 2d, 1w (at most 28 days)", MaxLength = 20)] string duration,
        [SlashCommandParameter(Description = "Why; the member sees this", MaxLength = 500)] string reason)
    {
        var length = Durations.Parse(duration);
        var refusal = await RefusalAsync(member)
            ?? (length is null ? "Durations look like 10m, 1h30m, 2d or 1w."
            : length > ModActions.MaxTimeout ? "Discord allows timeouts of up to 28 days." : null);
        await RunAsync(refusal, () => actions.TimeoutAsync(Me, member.Id, length!.Value, reason));
    }

    [SubSlashCommand("untimeout", "Lift a member's timeout")]
    [RequirePermission(BotPermissions.ModTimeout)]
    public async Task UntimeoutAsync(
        [SlashCommandParameter(Description = "Member")] GuildUser member,
        [SlashCommandParameter(Description = "Why", MaxLength = 500)] string? reason = null)
        => await RunAsync(await RefusalAsync(member), () => actions.UntimeoutAsync(Me, member.Id, reason));

    [SubSlashCommand("kick", "Kick a member out; they can come back with an invite")]
    [RequirePermission(BotPermissions.ModKick)]
    public async Task KickAsync(
        [SlashCommandParameter(Description = "Member")] GuildUser member,
        [SlashCommandParameter(Description = "Why; the member sees this", MaxLength = 500)] string reason)
        => await RunAsync(await RefusalAsync(member), () => actions.KickAsync(Me, member.Id, reason));

    [SubSlashCommand("ban", "Ban someone, for good or for a while; also someone who already left")]
    [RequirePermission(BotPermissions.ModBan)]
    public async Task BanAsync(
        [SlashCommandParameter(Description = "Member, or a user id")] User user,
        [SlashCommandParameter(Description = "Why; the member sees this", MaxLength = 500)] string reason,
        [SlashCommandParameter(Description = "How long, e.g. 1d or 2w (leave out: for good)", MaxLength = 20)] string? duration = null,
        [SlashCommandParameter(Name = "delete-messages", Description = "Also delete their recent messages")] DeleteMessages deleteMessages = DeleteMessages.None)
    {
        var length = duration is null ? null : Durations.Parse(duration);
        var refusal = await RefusalAsync(user as GuildUser, targetId: user.Id)
            ?? (duration is not null && length is null ? "Durations look like 1d, 2w or 12h." : null);
        var seconds = deleteMessages switch { DeleteMessages.LastHour => 3600, DeleteMessages.LastDay => 86_400, DeleteMessages.LastWeek => 604_800, _ => 0 };
        await RunAsync(refusal, () => actions.BanAsync(Me, user.Id, reason, length, seconds, member: user is GuildUser));
    }

    [SubSlashCommand("unban", "Lift a ban")]
    [RequirePermission(BotPermissions.ModBan)]
    public async Task UnbanAsync(
        [SlashCommandParameter(Description = "User id (or pick them)")] User user,
        [SlashCommandParameter(Description = "Why", MaxLength = 500)] string? reason = null)
        => await RunAsync(await RefusalAsync(null, targetId: user.Id), () => actions.UnbanAsync(Me, user.Id, reason));

    [SubSlashCommand("purge", "Delete recent messages here, optionally only someone's or with some text")]
    [RequirePermission(BotPermissions.ModMessages)]
    public async Task PurgeAsync(
        [SlashCommandParameter(Description = "At most this many", MinValue = 1, MaxValue = 500)] int count,
        [SlashCommandParameter(Description = "Only messages by this member")] User? from = null,
        [SlashCommandParameter(Description = "Only messages containing this", MaxLength = 100)] string? containing = null,
        [SlashCommandParameter(Name = "bots-only", Description = "Only messages by bots")] bool botsOnly = false,
        [SlashCommandParameter(Description = "Why", MaxLength = 500)] string? reason = null)
    {
        var refusal = await ModuleOffAsync() ?? (from is GuildUser member && member.Id != Actor.Id && !AccessControl.Outranks(Guild, Actor, member, allowEqual: false)
            ? $"<@{member.Id}> doesn't rank below you." : null);
        await RunAsync(refusal, () => actions.PurgeAsync(Me, Context.Channel.Id, count, from?.Id, containing, botsOnly, reason));
    }

    [SubSlashCommand("slowmode", "How often each member may post in a channel")]
    [RequirePermission(BotPermissions.ModChannels)]
    public async Task SlowmodeAsync(
        [SlashCommandParameter(Description = "Time between messages: 10s, 1m, 2h; 0 for off", MaxLength = 10)] string every,
        [SlashCommandParameter(Description = "Channel (default: this one)", AllowedChannelTypes = [ChannelType.TextGuildChannel, ChannelType.VoiceGuildChannel])] Channel? channel = null,
        [SlashCommandParameter(Description = "Why", MaxLength = 500)] string? reason = null)
    {
        var seconds = every.Trim() == "0" ? 0 : (int?)Durations.Parse(every)?.TotalSeconds;
        var refusal = await ModuleOffAsync() ?? (seconds is null or > 21_600 ? "Give a time like 10s, 1m or 2h (at most 6 hours), or 0 for off." : null);
        await RunAsync(refusal, () => actions.SlowmodeAsync(Me, channel?.Id ?? Context.Channel.Id, seconds!.Value, reason));
    }

    [SubSlashCommand("lock", "Stop everyone talking in a channel (or joining, for voice); roles allowed there keep it")]
    [RequirePermission(BotPermissions.ModChannels)]
    public async Task LockAsync(
        [SlashCommandParameter(Description = "Channel (default: this one)", AllowedChannelTypes = [ChannelType.TextGuildChannel, ChannelType.VoiceGuildChannel])] Channel? channel = null,
        [SlashCommandParameter(Description = "For how long, e.g. 30m (leave out: until unlocked)", MaxLength = 20)] string? duration = null,
        [SlashCommandParameter(Description = "Why", MaxLength = 500)] string? reason = null)
    {
        var length = duration is null ? null : Durations.Parse(duration);
        var refusal = await ModuleOffAsync() ?? (duration is not null && length is null ? "Durations look like 30m, 2h or 1d." : null);
        await RunAsync(refusal, () => actions.LockAsync(Me, channel?.Id ?? Context.Channel.Id, length, reason));
    }

    [SubSlashCommand("unlock", "Open a locked channel again")]
    [RequirePermission(BotPermissions.ModChannels)]
    public async Task UnlockAsync(
        [SlashCommandParameter(Description = "Channel (default: this one)", AllowedChannelTypes = [ChannelType.TextGuildChannel, ChannelType.VoiceGuildChannel])] Channel? channel = null,
        [SlashCommandParameter(Description = "Why", MaxLength = 500)] string? reason = null)
        => await RunAsync(await ModuleOffAsync(), () => actions.UnlockAsync(Me, channel?.Id ?? Context.Channel.Id, reason));

    [SubSlashCommand("move", "Move a member to another voice channel")]
    [RequirePermission(BotPermissions.ModVoice)]
    public async Task MoveAsync(
        [SlashCommandParameter(Description = "Member")] GuildUser member,
        [SlashCommandParameter(Description = "Voice channel", AllowedChannelTypes = [ChannelType.VoiceGuildChannel, ChannelType.StageGuildChannel])] Channel channel,
        [SlashCommandParameter(Description = "Why", MaxLength = 500)] string? reason = null)
        => await RunAsync(await RefusalAsync(member), () => actions.MoveAsync(Me, member.Id, channel.Id, reason));

    [SubSlashCommand("disconnect", "Disconnect a member from voice")]
    [RequirePermission(BotPermissions.ModVoice)]
    public async Task DisconnectAsync(
        [SlashCommandParameter(Description = "Member")] GuildUser member,
        [SlashCommandParameter(Description = "Why", MaxLength = 500)] string? reason = null)
        => await RunAsync(await RefusalAsync(member), () => actions.DisconnectAsync(Me, member.Id, reason));

    [SubSlashCommand("mute", "Server-mute a member in voice, or lift it")]
    [RequirePermission(BotPermissions.ModVoice)]
    public Task MuteAsync(
        [SlashCommandParameter(Description = "Member")] GuildUser member,
        [SlashCommandParameter(Description = "For how long, e.g. 10m (leave out: until lifted)", MaxLength = 20)] string? duration = null,
        [SlashCommandParameter(Description = "Lift the mute instead")] bool off = false,
        [SlashCommandParameter(Description = "Why", MaxLength = 500)] string? reason = null)
        => VoiceStateAsync(member, deafen: false, off, duration, reason);

    [SubSlashCommand("deafen", "Server-deafen a member in voice, or lift it")]
    [RequirePermission(BotPermissions.ModVoice)]
    public Task DeafenAsync(
        [SlashCommandParameter(Description = "Member")] GuildUser member,
        [SlashCommandParameter(Description = "For how long, e.g. 10m (leave out: until lifted)", MaxLength = 20)] string? duration = null,
        [SlashCommandParameter(Description = "Lift the deafen instead")] bool off = false,
        [SlashCommandParameter(Description = "Why", MaxLength = 500)] string? reason = null)
        => VoiceStateAsync(member, deafen: true, off, duration, reason);

    private async Task VoiceStateAsync(GuildUser member, bool deafen, bool off, string? duration, string? reason)
    {
        var length = duration is null || off ? null : Durations.Parse(duration);
        var refusal = await RefusalAsync(member) ?? (duration is not null && !off && length is null ? "Durations look like 10m, 1h or 2d." : null);
        await RunAsync(refusal, () => actions.VoiceStateAsync(Me, member.Id, deafen, on: !off, length, reason));
    }

    [SubSlashCommand("role", "Give or take a role, for good or for a while")]
    public sealed class RoleCommands(ModuleState modules, ModActions actions) : ApplicationCommandModule<ApplicationCommandContext>
    {
        [SubSlashCommand("add", "Give a member a role below your own")]
        [RequirePermission(BotPermissions.ModRoles)]
        public Task AddAsync(
            [SlashCommandParameter(Description = "Member")] GuildUser member,
            [SlashCommandParameter(Description = "Role")] Role role,
            [SlashCommandParameter(Description = "For how long, e.g. 1d (leave out: for good)", MaxLength = 20)] string? duration = null,
            [SlashCommandParameter(Description = "Why", MaxLength = 500)] string reason = "")
            => ChangeAsync(member, role, give: true, duration, reason);

        [SubSlashCommand("remove", "Take a role below your own from a member")]
        [RequirePermission(BotPermissions.ModRoles)]
        public Task RemoveAsync(
            [SlashCommandParameter(Description = "Member")] GuildUser member,
            [SlashCommandParameter(Description = "Role")] Role role,
            [SlashCommandParameter(Description = "For how long, e.g. 1d (leave out: for good)", MaxLength = 20)] string? duration = null,
            [SlashCommandParameter(Description = "Why", MaxLength = 500)] string reason = "")
            => ChangeAsync(member, role, give: false, duration, reason);

        private async Task ChangeAsync(GuildUser member, Role role, bool give, string? duration, string reason)
        {
            var guild = Context.Guild!;
            var actor = (GuildUser)Context.User;
            var length = duration is null ? null : Durations.Parse(duration);
            string? refusal = !await modules.IsEnabledAsync(guild.Id, ModuleId) ? $"The `{ModuleId}` module is off."
                : string.IsNullOrWhiteSpace(reason) ? "Give a reason."
                : member.Id == actor.Id ? "Not on yourself."
                : !AccessControl.Outranks(guild, actor, member, allowEqual: false) ? $"<@{member.Id}> doesn't rank below you."
                : role.Id == guild.Id || role.Managed ? "That role can't be given or taken by hand."
                : !RoleBelow(guild, actor, role) ? $"<@&{role.Id}> isn't below your own top role."
                : give == member.RoleIds.Contains(role.Id) ? $"<@{member.Id}> {(give ? "already has" : "doesn't have")} <@&{role.Id}>."
                : duration is not null && length is null ? "Durations look like 1h, 2d or 1w."
                : null;
            if (refusal is not null)
            {
                await RespondAsync(InteractionCallback.Message(Replies.Ephemeral(refusal)));
                return;
            }

            await RespondAsync(InteractionCallback.DeferredMessage(MessageFlags.Ephemeral));
            var result = await actions.RoleAsync(new(guild.Id, actor.Id, actor.Username), member.Id, role.Id, give, length, reason);
            await ModifyResponseAsync(m =>
            {
                m.Content = result;
                m.AllowedMentions = AllowedMentionsProperties.None;
            });
        }

        private static bool RoleBelow(Guild guild, GuildUser actor, Role role)
            => guild.OwnerId == actor.Id || actor.RoleIds.Any(id => guild.Roles.TryGetValue(id, out var own) && own.Position.CompareTo(role.Position) > 0);
    }

    [SubSlashCommand("automod", "Discord's own filters, which block messages before they're posted (needs mod.manage)")]
    [RequirePermission(BotPermissions.ModManage)]
    public sealed class AutoModCommands(AutoModSetup automod) : ApplicationCommandModule<ApplicationCommandContext>
    {
        [SubSlashCommand("filter", "Turn a filter on or off")]
        public Task FilterAsync(
            [SlashCommandParameter(Description = "Which filter")] AutoModFilter filter,
            [SlashCommandParameter(Description = "On or off")] bool on,
            [SlashCommandParameter(Description = "Also time the sender out, e.g. 10m (words, links, invites, mentions)", MaxLength = 20)] string? timeout = null,
            [SlashCommandParameter(Name = "mention-limit", Description = "Mentions: most people or roles one message may ping", MinValue = 1, MaxValue = 50)] int? mentionLimit = null)
        {
            var length = timeout is null ? null : Durations.Parse(timeout);
            if (timeout is not null && (length is null || length > ModActions.MaxTimeout))
                return Reply("Timeouts look like 10m, 1h or 2d (at most 28 days).");
            return RunAsync(() => automod.SetAsync(Context.Guild!.Id, filter, on, length, mentionLimit));
        }

        [SubSlashCommand("words", "Add or remove blocked words (comma-separated; *word* also matches inside other words)")]
        public Task WordsAsync(
            [SlashCommandParameter(Description = "Add or remove")] ListChange change,
            [SlashCommandParameter(Description = "Words, comma-separated", MaxLength = 1000)] string words)
            => RunAsync(() => automod.WordsAsync(Context.Guild!.Id, Split(words), change == ListChange.Add));

        [SubSlashCommand("allow-links", "Sites that may still be linked when the links filter is on")]
        public Task AllowLinksAsync(
            [SlashCommandParameter(Description = "Add or remove")] ListChange change,
            [SlashCommandParameter(Description = "Sites, comma-separated, e.g. youtube.com, twitch.tv", MaxLength = 1000)] string sites)
            => RunAsync(() => automod.AllowLinksAsync(Context.Guild!.Id, Split(sites), change == ListChange.Add));

        [SubSlashCommand("exempt", "A role the filters skip, or stop skipping")]
        public Task ExemptAsync(
            [SlashCommandParameter(Description = "Role")] Role role,
            [SlashCommandParameter(Description = "Skip it (or stop)")] bool exempt = true)
            => RunAsync(() => automod.ExemptRoleAsync(Context.Guild!.Id, role.Id, exempt, Context.User.Id));

        [SubSlashCommand("status", "Every AutoMod rule in the server")]
        public Task StatusAsync() => RunAsync(async () =>
        {
            var rules = await automod.RulesAsync(Context.Guild!.Id);
            return rules.Count == 0 ? "No AutoMod rules." : string.Join('\n', rules.Select(r =>
                $"{(r.Enabled ? "🟢" : "⚪")} **{r.Name}**"
                + (r.TriggerMetadata.KeywordFilter is { Count: > 0 } words ? $" · {words.Count} words" : "")
                + (r.TriggerMetadata.Presets is { Count: > 0 } presets ? $" · {string.Join(", ", presets)}" : "")
                + (r.TriggerMetadata.MentionTotalLimit is { } limit ? $" · at most {limit} mentions" : "")
                + (r.TriggerMetadata.AllowList is { Count: > 0 } allowed ? $" · allows {string.Join(", ", allowed.Select(a => a.Trim('*')))}" : "")
                + (r.Actions.FirstOrDefault(a => a.Type == AutoModerationActionType.Timeout)?.Metadata?.DurationSeconds is { } s ? $" · timeout {Durations.Format(TimeSpan.FromSeconds(s))}" : "")));
        });

        private static List<string> Split(string text) => text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

        private Task Reply(string text) => RespondAsync(InteractionCallback.Message(Replies.Ephemeral(text)));

        // Discord refuses rules the bot can't manage (it needs Manage Server) with a 403.
        private async Task RunAsync(Func<Task<string>> action)
        {
            await RespondAsync(InteractionCallback.DeferredMessage(MessageFlags.Ephemeral));
            string result;
            try
            {
                result = await action();
            }
            catch (RestException ex) when (ex.StatusCode is System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.BadRequest)
            {
                result = $"Discord refused: {ex.Message}";
            }
            await ModifyResponseAsync(m =>
            {
                m.Content = result;
                m.AllowedMentions = AllowedMentionsProperties.None;
            });
        }
    }

    [SubSlashCommand("note", "A private note on a member, for moderators only")]
    [RequirePermission(BotPermissions.ModWarn)]
    public async Task<InteractionMessageProperties> NoteAsync(
        [SlashCommandParameter(Description = "Member (or someone who left)")] User member,
        [SlashCommandParameter(Description = "The note", MaxLength = 1000)] string text)
    {
        if (await RefusalAsync(member as GuildUser, targetId: member.Id) is { } refusal)
            return Replies.Ephemeral(refusal);

        var (c, _) = await cases.OpenAsync(NewCase(CaseTypes.Note, member.Id, text), dm: false);
        return Replies.Ephemeral($"Case #{c.Number}: noted on <@{member.Id}>.");
    }

    [SubSlashCommand("history", "A member's cases and notes")]
    [RequirePermission(BotPermissions.ModWarn)]
    public Task<InteractionMessageProperties> HistoryAsync([SlashCommandParameter(Description = "Member (or someone who left)")] User member)
        => History.ForAsync(cases, Guild.Id, member);

    [SubSlashCommand("settings", "Moderation log, DMs to members, how long warnings count (needs mod.manage)")]
    [RequirePermission(BotPermissions.ModManage)]
    public async Task<InteractionMessageProperties> SettingsAsync(
        [SlashCommandParameter(Name = "log-channel", Description = "Where every case is posted", AllowedChannelTypes = [ChannelType.TextGuildChannel])] Channel? logChannel = null,
        [SlashCommandParameter(Name = "no-log", Description = "Stop posting cases")] bool noLog = false,
        [SlashCommandParameter(Name = "dm-members", Description = "Tell members about warnings, timeouts, kicks and bans")] bool? dmMembers = null,
        [SlashCommandParameter(Name = "dm-names-moderator", Description = "Say who did it in those DMs")] bool? dmNamesModerator = null,
        [SlashCommandParameter(Name = "warning-days", Description = "How long a warning counts", MinValue = 1, MaxValue = 3650)] int? warningDays = null)
    {
        var before = await settings.GetAsync<ModRules>(Guild.Id, ModuleId);
        var after = before with
        {
            LogChannelId = noLog ? null : logChannel?.Id ?? before.LogChannelId,
            DmMembers = dmMembers ?? before.DmMembers,
            DmNamesModerator = dmNamesModerator ?? before.DmNamesModerator,
            WarningDays = warningDays ?? before.WarningDays,
        };
        var changed = after != before;
        if (changed)
            await settings.SetAsync(Guild.Id, ModuleId, after, Actor.Id, SettingsStore.Diff(before, after));

        return Replies.Ephemeral($"""
            {(changed ? "Updated." : "Nothing changed.")}
            Moderation log: {(after.LogChannelId is { } log ? $"<#{log}>" : "none")}
            DMs to members: {(after.DmMembers ? $"yes, {(after.DmNamesModerator ? "naming" : "not naming")} the moderator" : "no")}
            Warnings count for {after.WarningDays} days{(after.Escalations.Count == 0 ? "" : "; then: " + string.Join(", ", after.Escalations.Select(Escalation.Describe)))}
            """);
    }

    [SubSlashCommand("case", "Look at or change a case")]
    public sealed class CaseCommands(CaseBook cases, AccessControl access) : ApplicationCommandModule<ApplicationCommandContext>
    {
        [SubSlashCommand("show", "Show a case")]
        [RequirePermission(BotPermissions.ModWarn)]
        public async Task<InteractionMessageProperties> ShowAsync([SlashCommandParameter(Description = "Case number", MinValue = 1)] int number)
            => await cases.FindAsync(Context.Guild!.Id, number) is { } c
                ? new() { Embeds = [Describe.Embed(c)], Flags = MessageFlags.Ephemeral, AllowedMentions = AllowedMentionsProperties.None }
                : Replies.Ephemeral("There's no such case.");

        [SubSlashCommand("reason", "Change a case's reason (yours, or any with mod.manage)")]
        public async Task<InteractionMessageProperties> ReasonAsync(
            [SlashCommandParameter(Description = "Case number", MinValue = 1)] int number,
            [SlashCommandParameter(Description = "The reason", MaxLength = 500)] string reason)
        {
            if (await CaseRefusalAsync(cases, access, Context, number) is { } refusal)
                return Replies.Ephemeral(refusal);
            await cases.ChangeAsync(Context.Guild!.Id, number, Context.User.Id, $"reason: {reason}", c => c.Reason = reason.Trim());
            return Replies.Ephemeral($"Case #{number}'s reason is changed.");
        }

        [SubSlashCommand("pardon", "Pardon a warning: it stops counting, and stays in the history")]
        public async Task<InteractionMessageProperties> PardonAsync(
            [SlashCommandParameter(Description = "Case number", MinValue = 1)] int number,
            [SlashCommandParameter(Description = "Why", MaxLength = 500)] string? reason = null)
            => Replies.Ephemeral(await Pardon.RunAsync(cases, access, Context.Guild!, (GuildUser)Context.User, number, reason));
    }

    // Changing a case is for whoever made it, or mod.manage.
    internal static async Task<string?> CaseRefusalAsync(CaseBook cases, AccessControl access, ApplicationCommandContext context, int number)
    {
        if (await cases.FindAsync(context.Guild!.Id, number) is not { } c)
            return "There's no such case.";
        return c.ModeratorId == context.User.Id || await access.CanAsync(context.Guild, (GuildUser)context.User, BotPermissions.ModManage)
            ? null
            : $"Only <@{c.ModeratorId}> or someone with `{BotPermissions.ModManage}` can change it.";
    }

    private Actor Me => new(Guild.Id, Actor.Id, Actor.Username);

    // Discord calls take a moment, so the reply is deferred once the checks pass.
    private async Task RunAsync(string? refusal, Func<Task<string>> action)
    {
        if (refusal is not null)
        {
            await RespondAsync(InteractionCallback.Message(Replies.Ephemeral(refusal)));
            return;
        }
        await RespondAsync(InteractionCallback.DeferredMessage(MessageFlags.Ephemeral));
        var result = await action();
        await ModifyResponseAsync(m =>
        {
            m.Content = result;
            m.AllowedMentions = AllowedMentionsProperties.None;
        });
    }

    private ModCase NewCase(string type, ulong targetId, string reason) => new()
    {
        GuildId = Guild.Id,
        Type = type,
        TargetId = targetId,
        ModeratorId = Actor.Id,
        Reason = reason.Trim(),
        CreatedAt = time.GetUtcNow(),
    };

    private async Task<string?> ModuleOffAsync()
        => await modules.IsEnabledAsync(Guild.Id, ModuleId) ? null : $"The `{ModuleId}` module is off.";

    // Not on yourself, and only on members ranked below you; also for the module being off.
    private async Task<string?> RefusalAsync(GuildUser? member, ulong? targetId = null)
    {
        if (await ModuleOffAsync() is { } off)
            return off;
        if ((member?.Id ?? targetId) == Actor.Id)
            return "Not on yourself.";
        if (member is not null && !AccessControl.Outranks(Guild, Actor, member, allowEqual: false))
            return $"<@{member.Id}> doesn't rank below you.";
        return null;
    }

    private static string Dm(bool dmed) => dmed ? " They got a DM." : " They didn't get a DM (closed, or DMs are off in `/mod settings`).";

    [SubSlashCommand("nick", "Change or reset a member's nickname")]
    [RequirePermission(BotPermissions.ModerateNicknames)]
    public async Task<InteractionMessageProperties> NickAsync(
        [SlashCommandParameter(Description = "Member")] GuildUser user,
        [SlashCommandParameter(Description = "New nickname (leave out to reset it)", MaxLength = 32)] string? name = null)
    {
        var guild = Context.Guild!;
        var actor = (GuildUser)Context.User;

        if (!await modules.IsEnabledAsync(guild.Id, ModuleId))
            return Replies.Ephemeral($"The `{ModuleId}` module is off.");
        if (user.Id == actor.Id)
            return Replies.Ephemeral("Not on yourself.");
        if (!AccessControl.Outranks(guild, actor, user, allowEqual: false))
            return Replies.Ephemeral($"<@{user.Id}> doesn't rank below you.");

        name = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        try
        {
            await user.ModifyAsync(u => u.Nickname = name ?? "");
        }
        catch (RestException ex) when (ex.StatusCode == HttpStatusCode.Forbidden)
        {
            return Replies.Ephemeral($"Discord won't let me rename <@{user.Id}>: bots can't rename the owner, or anyone whose top role is above the bot's.");
        }

        await using var db = await dbFactory.CreateDbContextAsync();
        db.AuditEntries.Add(new()
        {
            GuildId = guild.Id,
            ActorId = actor.Id,
            Action = "mod.nick",
            Details = $"{user.Id} {user.Nickname ?? "(none)"} -> {name ?? "(none)"}",
            CreatedAt = time.GetUtcNow(),
        });
        await db.SaveChangesAsync();

        return Replies.Ephemeral(name is null ? $"Reset <@{user.Id}>'s nickname." : $"Renamed <@{user.Id}> to **{name}**.");
    }
}

public enum DeleteMessages
{
    None,
    LastHour,
    LastDay,
    LastWeek,
}

public enum EscalationChoice
{
    Timeout,
    Kick,
    Ban,
    [SlashCommandChoice(Name = "Nothing (remove this step)")]
    Nothing,
}

public enum ListChange
{
    Add,
    Remove,
}
