using System.Net;

using NetCord.Rest;

namespace THOBOTTO.Moderation;

// Timeouts, kicks and bans: does them on Discord and records the case. Shared by the commands,
// the log's Lift buttons and the timer that ends temporary bans. Each returns what to tell the moderator.
public sealed class ModActions(CaseBook cases, RestClient rest, TimeProvider time)
{
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
    public ModCase Case(string type, ulong targetId, string? reason, DateTimeOffset now) => new()
    {
        GuildId = GuildId,
        Type = type,
        TargetId = targetId,
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
