using System.Net;

using NetCord;
using NetCord.Rest;

using THOBOTTO.Archive;

namespace THOBOTTO.Moderation;

// Timeouts, kicks and bans: does them on Discord and records the case. Shared by the commands,
// the log's Lift buttons and the timer that ends temporary bans. Each returns what to tell the moderator.
public sealed class ModActions(CaseBook cases, RestClient rest, DeletionWitness witness, TimeProvider time)
{
    // How far back a purge looks for messages that match.
    private const int PurgeScan = 500;

    // What a lock takes from @everyone: talking in a text channel, joining a voice one.
    private const Permissions TextLock = Permissions.SendMessages | Permissions.SendMessagesInThreads | Permissions.CreatePublicThreads | Permissions.CreatePrivateThreads | Permissions.AddReactions;
    private const Permissions VoiceLock = Permissions.Connect;

    // Discord's longest timeout.
    public static readonly TimeSpan MaxTimeout = TimeSpan.FromDays(28);

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
        if (await DiscordAsync(() => rest.ModifyGuildUserAsync(actor.GuildId, targetId, u => u.TimeOutUntil = null, actor.Audit(reason)), targetId) is { } problem)
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

        var c = actor.Case(CaseTypes.Warn, message.Author.Id, reason, time.GetUtcNow(), message.ChannelId);
        c.Details = string.IsNullOrWhiteSpace(message.Content) ? "(a message without text)" : message.Content;
        var (opened, dmed) = await cases.OpenAsync(c);
        return $"Case #{opened.Number}: deleted the message and warned <@{message.Author.Id}>.{Dm(dmed)}";
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

    private static string Dm(bool dmed) => dmed ? " They got a DM." : " They didn't get a DM (closed, or DMs are off in `/mod settings`).";
}

// Who acts, for a case and for Discord's own audit log (which would otherwise only show the bot).
public sealed record Actor(ulong GuildId, ulong Id, string Name)
{
    public ModCase Case(string type, ulong targetId, string? reason, DateTimeOffset now, ulong? channelId = null) => new()
    {
        GuildId = GuildId,
        Type = type,
        TargetId = targetId,
        ChannelId = channelId,
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
