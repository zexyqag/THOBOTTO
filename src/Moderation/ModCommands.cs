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
    public async Task<InteractionMessageProperties> WarnAsync(
        [SlashCommandParameter(Description = "Member")] GuildUser member,
        [SlashCommandParameter(Description = "Why; the member sees this", MaxLength = 500)] string reason)
    {
        if (await RefusalAsync(member) is { } refusal)
            return Replies.Ephemeral(refusal);

        var (c, dmed) = await cases.OpenAsync(NewCase(CaseTypes.Warn, member.Id, reason));
        var active = await cases.ActiveWarningsAsync(Guild.Id, member.Id);
        return Replies.Ephemeral($"Case #{c.Number}: warned <@{member.Id}> ({active} active warning{(active == 1 ? "" : "s")}).{Dm(dmed)}");
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
            Warnings count for {after.WarningDays} days
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

    // Not on yourself, and only on members ranked below you; also for the module being off.
    private async Task<string?> RefusalAsync(GuildUser? member, ulong? targetId = null)
    {
        if (!await modules.IsEnabledAsync(Guild.Id, ModuleId))
            return $"The `{ModuleId}` module is off.";
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
