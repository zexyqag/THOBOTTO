using NetCord;
using NetCord.Gateway;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;

using THOBOTTO.Access;
using THOBOTTO.Moderation;
using THOBOTTO.Modules;

namespace THOBOTTO;

public sealed partial class SetupCommands
{
    [SubSlashCommand("moderation", "Moderation log, DMs, warnings that add up (needs mod.manage)")]
    [RequirePermission(BotPermissions.ModManage)]
    public sealed class ModerationSetup(SettingsStore settings) : ApplicationCommandModule<ApplicationCommandContext>
    {
        private const string ModuleId = CaseBook.ModuleId;

        private Guild Guild => Context.Guild!;

        private GuildUser Actor => (GuildUser)Context.User;

        [SubSlashCommand("general", "Moderation log, DMs to members, how long warnings count")]
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

        [SubSlashCommand("escalate", "What happens on its own as warnings add up")]
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
            var after = before with { Escalations = Escalation.With(before.Escalations, warnings, action == EscalationChoice.Nothing ? null : action.ToString().ToLowerInvariant(), length) };
            await settings.SetAsync(Guild.Id, ModuleId, after, Actor.Id, $"escalation at {warnings}: {action} {duration}");

            return Replies.Ephemeral(after.Escalations.Count == 0
                ? "Warnings don't lead to anything on their own."
                : $"As warnings add up (counting {after.WarningDays} days):\n" + string.Join('\n', after.Escalations.Select(s => "• " + Escalation.Describe(s))));
        }
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
}
