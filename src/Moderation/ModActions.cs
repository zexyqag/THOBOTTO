using System.Net;

using NetCord;
using NetCord.Rest;

using THOBOTTO.Archive;

namespace THOBOTTO.Moderation;

// Timeouts, kicks and bans: does them on Discord and records the case. Shared by the commands,
// the log's Lift buttons and the timer that ends temporary bans. Each returns what to tell the moderator.
public sealed class ModActions(
    CaseBook cases,
    RestClient rest,
    DeletionWitness witness,
    THOBOTTO.Voice.VoicePresence presence,
    THOBOTTO.Modules.SettingsStore settings,
    NetCord.Gateway.GatewayClient gateway,
    TimeProvider time)
{
    // How far back a purge looks for messages that match.
    private const int PurgeScan = 500;

    // What a lock takes from @everyone: talking in a text channel, joining a voice one.
    private const Permissions TextLock = Permissions.SendMessages | Permissions.SendMessagesInThreads | Permissions.CreatePublicThreads | Permissions.CreatePrivateThreads | Permissions.AddReactions;
    private const Permissions VoiceLock = Permissions.Connect;

    // Discord's longest timeout.
    public static readonly TimeSpan MaxTimeout = TimeSpan.FromDays(28);

    // Warns a member; if that brings their active warnings to a step, the bot takes that step.
    public async Task<string> WarnAsync(Actor actor, ulong targetId, string reason, string? details = null, ulong? channelId = null)
    {
        var c = actor.Case(CaseTypes.Warn, targetId, reason, time.GetUtcNow(), channelId);
        c.Details = details;
        var (opened, dmed) = await cases.OpenAsync(c);
        var active = await cases.ActiveWarningsAsync(actor.GuildId, targetId);
        var text = $"Case #{opened.Number}: warned <@{targetId}> ({active} active warning{(active == 1 ? "" : "s")}).{Dm(dmed)}";

        var rules = await settings.GetAsync<ModRules>(actor.GuildId, CaseBook.ModuleId);
        if (Escalation.StepFor(rules.Escalations, active) is not { } step || gateway.Cache.User is not { } bot)
            return text;
        var me = new Actor(actor.GuildId, bot.Id, bot.Username);
        var why = $"{active} active warnings, the latest case #{opened.Number}";
        var duration = step.Minutes is { } m ? TimeSpan.FromMinutes(m) : (TimeSpan?)null;
        var stepped = step.Action switch
        {
            EscalationActions.Timeout => await TimeoutAsync(me, targetId, duration ?? TimeSpan.FromHours(1), why),
            EscalationActions.Kick => await KickAsync(me, targetId, why),
            _ => await BanAsync(me, targetId, why, duration, 0, member: true),
        };
        return $"{text}\nThat's {active}: {stepped}";
    }

    public async Task<string> TimeoutAsync(Actor actor, ulong targetId, TimeSpan duration, string reason)
    {
        var c = actor.Case(CaseTypes.Timeout, targetId, reason, time.GetUtcNow());
        c.EndsAt = c.CreatedAt + duration;
        if (await DiscordAsync(() => rest.ModifyGuildUserAsync(actor.GuildId, targetId, u => u.TimeOutUntil = c.EndsAt, actor.Audit(reason)), targetId) is { } problem)
            return problem;

        // A new timeout replaces any running one.
        await cases.EndAsync(actor.GuildId, targetId, CaseTypes.Timeout);
        var (opened, dmed) = await cases.OpenAsync(c);
        return $"Case #{opened.Number}: timed out <@{targetId}> for {Durations.Format(duration)}.{Dm(dmed)}";
    }

    public async Task<string> UntimeoutAsync(Actor actor, ulong targetId, string? reason)
    {
        if (await DiscordAsync(() => ClearMemberFieldAsync(actor.GuildId, targetId, "communication_disabled_until", actor.Audit(reason)), targetId) is { } problem)
            return problem;

        await cases.EndAsync(actor.GuildId, targetId, CaseTypes.Timeout);
        var (opened, dmed) = await cases.OpenAsync(actor.Case(CaseTypes.Untimeout, targetId, reason, time.GetUtcNow()));
        return $"Case #{opened.Number}: lifted <@{targetId}>'s timeout.{Dm(dmed)}";
    }

    public async Task<string> KickAsync(Actor actor, ulong targetId, string reason)
    {
        var c = actor.Case(CaseTypes.Kick, targetId, reason, time.GetUtcNow());
        // Told first: once out, the bot may share no server with them to DM from.
        var dmed = await cases.TellAsync(c);
        if (await DiscordAsync(() => rest.KickGuildUserAsync(actor.GuildId, targetId, actor.Audit(reason)), targetId) is { } problem)
            return problem;

        var (opened, _) = await cases.OpenAsync(c, dm: false);
        return $"Case #{opened.Number}: kicked <@{targetId}>.{Dm(dmed)}";
    }

    public async Task<string> BanAsync(Actor actor, ulong targetId, string reason, TimeSpan? duration, int deleteMessageSeconds, bool member)
    {
        var c = actor.Case(CaseTypes.Ban, targetId, reason, time.GetUtcNow());
        c.EndsAt = c.CreatedAt + duration;
        var dmed = member && await cases.TellAsync(c);
        if (await DiscordAsync(() => rest.BanGuildUserAsync(actor.GuildId, targetId, deleteMessageSeconds, actor.Audit(reason)), targetId) is { } problem)
            return problem;

        await cases.EndAsync(actor.GuildId, targetId, CaseTypes.Ban);
        var (opened, _) = await cases.OpenAsync(c, dm: false);
        return $"Case #{opened.Number}: banned <@{targetId}> {(duration is { } d ? $"for {Durations.Format(d)}" : "for good")}.{(member ? Dm(dmed) : "")}";
    }

    public async Task<string> UnbanAsync(Actor actor, ulong targetId, string? reason)
    {
        if (await DiscordAsync(() => rest.UnbanGuildUserAsync(actor.GuildId, targetId, actor.Audit(reason)), targetId) is { } problem)
            return problem;

        await cases.EndAsync(actor.GuildId, targetId, CaseTypes.Ban);
        var (opened, _) = await cases.OpenAsync(actor.Case(CaseTypes.Unban, targetId, reason, time.GetUtcNow()), dm: false);
        return $"Case #{opened.Number}: unbanned <@{targetId}>.";
    }

    // Deletes up to count recent messages in a channel that match. Discord only bulk-deletes messages
    // under 14 days old; older ones are left. The archive keeps copies, marked deleted by the moderator.
    public async Task<string> PurgeAsync(Actor actor, ulong channelId, int count, ulong? fromId, string? containing, bool botsOnly, string? reason)
    {
        var cutoff = time.GetUtcNow() - TimeSpan.FromDays(14) + TimeSpan.FromMinutes(5);
        var picked = new List<ulong>();
        var scanned = 0;
        await foreach (var message in rest.GetMessagesAsync(channelId, new() { BatchSize = 100 }))
        {
            if (++scanned > PurgeScan || message.CreatedAt < cutoff || picked.Count >= count)
                break;
            if ((fromId is null || message.Author.Id == fromId) && (!botsOnly || message.Author.IsBot)
                && (containing is null || message.Content.Contains(containing, StringComparison.OrdinalIgnoreCase)))
                picked.Add(message.Id);
        }
        if (picked.Count == 0)
            return "No recent messages match (Discord only lets bots bulk-delete messages under 14 days old).";

        witness.Expect(picked, actor.Id);
        try
        {
            foreach (var chunk in picked.Chunk(100))
            {
                if (chunk.Length == 1)
                    await rest.DeleteMessageAsync(channelId, chunk[0], actor.Audit(reason));
                else
                    await rest.DeleteMessagesAsync(channelId, chunk, actor.Audit(reason));
            }
        }
        catch (RestException ex) when (ex.StatusCode == HttpStatusCode.Forbidden)
        {
            return "Discord won't let me delete messages there (the bot needs Manage Messages in that channel).";
        }

        var c = actor.Case(CaseTypes.Purge, fromId ?? channelId, reason, time.GetUtcNow(), channelId);
        c.Details = $"{picked.Count} message{(picked.Count == 1 ? "" : "s")}" + (containing is null ? "" : $" containing \"{containing}\"") + (botsOnly ? " from bots" : "");
        var (opened, _) = await cases.OpenAsync(c, dm: false);
        return $"Case #{opened.Number}: deleted {c.Details}.";
    }

    public async Task<string> SlowmodeAsync(Actor actor, ulong channelId, int seconds, string? reason)
    {
        try
        {
            await rest.ModifyGuildChannelAsync(channelId, o => o.Slowmode = seconds, actor.Audit(reason));
        }
        catch (RestException ex) when (ex.StatusCode == HttpStatusCode.Forbidden)
        {
            return "Discord won't let me change that channel (the bot needs Manage Channels there).";
        }

        var c = actor.Case(CaseTypes.Slowmode, channelId, reason, time.GetUtcNow(), channelId);
        c.Details = seconds == 0 ? "off" : $"one message per {Durations.Format(TimeSpan.FromSeconds(seconds))}";
        var (opened, _) = await cases.OpenAsync(c, dm: false);
        return $"Case #{opened.Number}: slowmode in <#{channelId}> {c.Details}.";
    }

    // Takes talking (or joining, for voice) from @everyone in a channel; roles allowed it there keep it.
    public async Task<string> LockAsync(Actor actor, ulong channelId, TimeSpan? duration, string? reason)
    {
        if (await rest.GetChannelAsync(channelId) is not IGuildChannel channel)
            return "That isn't a server channel.";
        var locked = channel is VoiceGuildChannel or StageGuildChannel ? VoiceLock : TextLock;
        var everyone = channel.PermissionOverwrites.GetValueOrDefault(actor.GuildId);
        if (everyone is not null && (everyone.Denied & locked) == locked)
            return $"<#{channelId}> is already locked.";

        if (await DiscordAsync(() => rest.ModifyGuildChannelPermissionsAsync(channelId, new(actor.GuildId, PermissionOverwriteType.Role)
        {
            Allowed = (everyone?.Allowed ?? 0) & ~locked,
            Denied = (everyone?.Denied ?? 0) | locked,
        }, actor.Audit(reason)), channelId) is { } problem)
            return problem;

        var c = actor.Case(CaseTypes.Lock, channelId, reason, time.GetUtcNow(), channelId);
        c.EndsAt = c.CreatedAt + duration;
        await cases.EndAsync(actor.GuildId, channelId, CaseTypes.Lock);
        var (opened, _) = await cases.OpenAsync(c, dm: false);
        await NoticeAsync(channel, $"🔒 This channel is locked{(c.EndsAt is { } until ? $" until <t:{until.ToUnixTimeSeconds()}:t>" : "")}.");
        return $"Case #{opened.Number}: locked <#{channelId}>{(duration is { } d ? $" for {Durations.Format(d)}" : "")}.";
    }

    public async Task<string> UnlockAsync(Actor actor, ulong channelId, string? reason)
    {
        if (await rest.GetChannelAsync(channelId) is not IGuildChannel channel)
            return "That isn't a server channel.";
        var locked = channel is VoiceGuildChannel or StageGuildChannel ? VoiceLock : TextLock;
        if (channel.PermissionOverwrites.GetValueOrDefault(actor.GuildId) is not { } everyone || (everyone.Denied & locked) == 0)
        {
            await cases.EndAsync(actor.GuildId, channelId, CaseTypes.Lock);
            return $"<#{channelId}> isn't locked.";
        }

        if (await DiscordAsync(() => rest.ModifyGuildChannelPermissionsAsync(channelId, new(actor.GuildId, PermissionOverwriteType.Role)
        {
            Allowed = everyone.Allowed,
            Denied = everyone.Denied & ~locked,
        }, actor.Audit(reason)), channelId) is { } problem)
            return problem;

        await cases.EndAsync(actor.GuildId, channelId, CaseTypes.Lock);
        var (opened, _) = await cases.OpenAsync(actor.Case(CaseTypes.Unlock, channelId, reason, time.GetUtcNow(), channelId), dm: false);
        await NoticeAsync(channel, "🔓 This channel is open again.");
        return $"Case #{opened.Number}: unlocked <#{channelId}>.";
    }

    // Removes a message and warns its author about it, keeping its text in the case.
    public async Task<string> DeleteAndWarnAsync(Actor actor, RestMessage message, string reason)
    {
        witness.Expect([message.Id], actor.Id);
        if (await DiscordAsync(() => rest.DeleteMessageAsync(message.ChannelId, message.Id, actor.Audit(reason)), message.Author.Id) is { } problem)
            return problem;

        var details = string.IsNullOrWhiteSpace(message.Content) ? "(a message without text)" : message.Content;
        return "Deleted the message. " + await WarnAsync(actor, message.Author.Id, reason, details, message.ChannelId);
    }

    public async Task<string> MoveAsync(Actor actor, ulong targetId, ulong channelId, string? reason)
    {
        if (await VoiceAsync(() => rest.ModifyGuildUserAsync(actor.GuildId, targetId, u => u.ChannelId = channelId, actor.Audit(reason)), targetId) is { } problem)
            return problem;
        var (opened, _) = await cases.OpenAsync(actor.Case(CaseTypes.Move, targetId, reason, time.GetUtcNow(), channelId), dm: false);
        return $"Case #{opened.Number}: moved <@{targetId}> to <#{channelId}>.";
    }

    public async Task<string> DisconnectAsync(Actor actor, ulong targetId, string? reason)
    {
        // Discord accepts disconnecting someone who isn't in voice.
        if (!presence.Snapshot(actor.GuildId).ContainsKey(targetId))
            return $"<@{targetId}> isn't in a voice channel.";
        if (await VoiceAsync(() => ClearMemberFieldAsync(actor.GuildId, targetId, "channel_id", actor.Audit(reason)), targetId) is { } problem)
            return problem;
        var (opened, _) = await cases.OpenAsync(actor.Case(CaseTypes.Disconnect, targetId, reason, time.GetUtcNow()), dm: false);
        return $"Case #{opened.Number}: disconnected <@{targetId}> from voice.";
    }

    // Server mute or deafen, on or off; on can last a while.
    public async Task<string> VoiceStateAsync(Actor actor, ulong targetId, bool deafen, bool on, TimeSpan? duration, string? reason)
    {
        var call = () => rest.ModifyGuildUserAsync(actor.GuildId, targetId, u =>
        {
            if (deafen)
                u.Deafened = on;
            else
                u.Muted = on;
        }, actor.Audit(reason));
        if (await VoiceAsync(call, targetId) is { } problem)
            return problem;

        var type = (deafen, on) switch { (true, true) => CaseTypes.Deafen, (true, false) => CaseTypes.Undeafen, (false, true) => CaseTypes.Mute, _ => CaseTypes.Unmute };
        var c = actor.Case(type, targetId, reason, time.GetUtcNow());
        c.EndsAt = on ? c.CreatedAt + duration : null;
        await cases.EndAsync(actor.GuildId, targetId, deafen ? CaseTypes.Deafen : CaseTypes.Mute);
        var (opened, _) = await cases.OpenAsync(c, dm: false);
        return $"Case #{opened.Number}: {Describe.Label(type).Split(' ', 2)[1].ToLowerInvariant()} <@{targetId}>{(duration is { } d && on ? $" for {Durations.Format(d)}" : "")}.";
    }

    // Gives or takes a role; with a duration it's undone later.
    public async Task<string> RoleAsync(Actor actor, ulong targetId, ulong roleId, bool give, TimeSpan? duration, string? reason)
    {
        var call = give
            ? () => rest.AddGuildUserRoleAsync(actor.GuildId, targetId, roleId, actor.Audit(reason))
            : (Func<Task>)(() => rest.RemoveGuildUserRoleAsync(actor.GuildId, targetId, roleId, actor.Audit(reason)));
        if (await DiscordAsync(call, targetId) is { } problem)
            return problem;

        var c = actor.Case(give ? CaseTypes.RoleAdd : CaseTypes.RoleRemove, targetId, reason, time.GetUtcNow(), roleId: roleId);
        c.EndsAt = c.CreatedAt + duration;
        // The opposite change of the same role, if one was lasting, is over now.
        await cases.EndAsync(actor.GuildId, targetId, give ? CaseTypes.RoleRemove : CaseTypes.RoleAdd, roleId);
        await cases.EndAsync(actor.GuildId, targetId, c.Type, roleId);
        var (opened, _) = await cases.OpenAsync(c, dm: false);
        return $"Case #{opened.Number}: {(give ? "gave" : "took")} <@&{roleId}> {(give ? "to" : "from")} <@{targetId}>{(duration is { } d ? $" for {Durations.Format(d)}" : "")}.";
    }

    // Undoes a lasting case: from the log's button, or when its time is up.
    public Task<string> LiftAsync(Actor actor, ModCase c, string reason) => c.Type switch
    {
        CaseTypes.Ban => UnbanAsync(actor, c.TargetId, reason),
        CaseTypes.Lock => UnlockAsync(actor, c.TargetId, reason),
        CaseTypes.Timeout => UntimeoutAsync(actor, c.TargetId, reason),
        CaseTypes.Mute or CaseTypes.Deafen => VoiceStateAsync(actor, c.TargetId, c.Type == CaseTypes.Deafen, on: false, null, reason),
        CaseTypes.RoleAdd or CaseTypes.RoleRemove => RoleAsync(actor, c.TargetId, c.RoleId!.Value, give: c.Type == CaseTypes.RoleRemove, null, reason),
        _ => Task.FromResult("That case can't be lifted."),
    };

    // NetCord leaves null fields out, but lifting a timeout or disconnecting needs an explicit null.
    private async Task ClearMemberFieldAsync(ulong guildId, ulong userId, string field, RestRequestProperties properties)
    {
        using var content = new StringContent($"{{\"{field}\":null}}", System.Text.Encoding.UTF8, "application/json");
        await using var _ = await rest.SendRequestAsync(HttpMethod.Patch, content, $"/guilds/{guildId}/members/{userId}", null, new(guildId, null!), properties);
    }

    // Voice actions fail when the member isn't in voice.
    private static async Task<string?> VoiceAsync(Func<Task> call, ulong targetId)
    {
        try
        {
            return await DiscordAsync(call, targetId);
        }
        catch (RestException ex) when (ex.StatusCode == HttpStatusCode.BadRequest)
        {
            return $"<@{targetId}> isn't in a voice channel.";
        }
    }

    // Tells a text channel's members why they can't talk, or that they can again.
    private async Task NoticeAsync(IGuildChannel channel, string text)
    {
        if (channel is not TextGuildChannel)
            return;
        try
        {
            await rest.SendMessageAsync(channel.Id, new() { Content = text });
        }
        catch (RestException)
        {
        }
    }

    private static async Task<string?> DiscordAsync(Func<Task> call, ulong targetId)
    {
        try
        {
            await call();
            return null;
        }
        catch (RestException ex) when (ex.StatusCode == HttpStatusCode.Forbidden)
        {
            return $"Discord won't let me do that to <@{targetId}>: the bot's role has to be above theirs, and nobody can act on the owner or (for timeouts) an admin.";
        }
        catch (RestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return $"<@{targetId}> isn't there (not a member, or not banned).";
        }
    }

    private static string Dm(bool dmed) => dmed ? " They got a DM." : " They didn't get a DM (closed, or DMs are off in `/setup moderation general`).";
}

// Who acts, for a case and for Discord's own audit log (which would otherwise only show the bot).
public sealed record Actor(ulong GuildId, ulong Id, string Name)
{
    public ModCase Case(string type, ulong targetId, string? reason, DateTimeOffset now, ulong? channelId = null, ulong? roleId = null) => new()
    {
        GuildId = GuildId,
        Type = type,
        TargetId = targetId,
        ChannelId = channelId,
        RoleId = roleId,
        ModeratorId = Id,
        Reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim(),
        CreatedAt = now,
    };

    public RestRequestProperties Audit(string? reason)
    {
        var text = $"{Name}: {reason ?? "no reason given"}";
        return new() { AuditLogReason = text.Length <= 512 ? text : text[..512] };
    }
}
