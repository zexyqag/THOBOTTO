using System.Collections.Concurrent;
using System.Net;

using Microsoft.EntityFrameworkCore;

using NetCord;
using NetCord.Gateway;
using NetCord.Rest;

using THOBOTTO.Data;
using THOBOTTO.Moderation;
using THOBOTTO.Modules;

namespace THOBOTTO.Gate;

// The door: new members verify in a waiting room (if the server wants that); many joining at once is a raid,
// whose newcomers are held (or timed out, kicked, or only reported) while Discord's invites are paused and its
// verification raised; too-new accounts can be held too. Moderators hear about it in the gate channel and let
// people in or kick them there.
public sealed class Gatekeeper(
    IDbContextFactory<BotDbContext> dbFactory,
    SettingsStore settings,
    ModuleState modules,
    RestClient rest,
    GatewayClient gateway,
    ModActions actions,
    THOBOTTO.Discord.DiscordApi discord,
    TimeProvider time,
    ILogger<Gatekeeper> logger) : BackgroundService
{
    public const string ModuleId = "gate";
    public const string VerifyButton = "gateverify";

    // Server → recent joins, for noticing a raid.
    private readonly ConcurrentDictionary<ulong, List<(ulong User, DateTimeOffset At)>> _joins = new();

    public ValueTask<GateRules> RulesAsync(ulong guildId) => settings.GetAsync<GateRules>(guildId, ModuleId);

    public async Task JoinedAsync(GuildUser user)
    {
        if (user.IsBot || !await modules.IsEnabledAsync(user.GuildId, ModuleId))
            return;
        var rules = await RulesAsync(user.GuildId);
        var now = time.GetUtcNow();
        var joins = _joins.GetOrAdd(user.GuildId, _ => []);
        List<ulong> recent;
        lock (joins)
        {
            joins.Add((user.Id, now));
            joins.RemoveAll(j => now - j.At > TimeSpan.FromSeconds(rules.RaidSeconds));
            recent = joins.Select(j => j.User).ToList();
        }

        await using var db = await dbFactory.CreateDbContextAsync();
        if (await db.GateRaids.FindAsync(user.GuildId) is { } raid)
        {
            raid.LastJoinAt = now;
            await db.SaveChangesAsync();
            await ApplyAsync(user.GuildId, user.Id, HoldReasons.Raid, rules.RaidAction, rules);
            return;
        }
        if (rules.RaidJoins > 0 && recent.Count >= rules.RaidJoins)
        {
            await StartRaidAsync(user.GuildId, null, $"{recent.Count} joined within {rules.RaidSeconds} s", recent);
            return;
        }
        if (rules.MinAccountDays > 0 && now - user.CreatedAt < TimeSpan.FromDays(rules.MinAccountDays))
        {
            await ApplyAsync(user.GuildId, user.Id, HoldReasons.Young, rules.YoungAction, rules);
            return;
        }
        if (rules.Verify != VerifyModes.Off)
            await HoldAsync(user.GuildId, user.Id, HoldReasons.Verify, rules);
    }

    // What the Verify button does for this member: a reply, or (for the question) null to ask it.
    public async Task<string?> VerifyAsync(GuildUser user)
    {
        var rules = await RulesAsync(user.GuildId);
        await using var db = await dbFactory.CreateDbContextAsync();
        if (await db.GateHolds.FindAsync(user.GuildId, user.Id) is not { } hold)
            return "You're in already.";
        if (hold.Reason == HoldReasons.Raid && await db.GateRaids.FindAsync(user.GuildId) is not null)
            return "Lots of people are joining right now, so newcomers wait a little. A moderator will let you in.";
        if (hold.Reason == HoldReasons.Young)
            return "Your Discord account is quite new, so a moderator will let you in.";
        switch (rules.Verify)
        {
            case VerifyModes.Question when rules.Question is not null:
                return null;
            case VerifyModes.Mods:
                return "A moderator will let you in shortly.";
            default:
                await LetInAsync(user.GuildId, user.Id, null);
                return "Welcome in! 🎉";
        }
    }

    public async Task<string> AnswerAsync(GuildUser user, string answer)
    {
        var rules = await RulesAsync(user.GuildId);
        if (rules.Answers.Any(a => string.Equals(a.Trim(), answer.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            await LetInAsync(user.GuildId, user.Id, null);
            return "That's right. Welcome in! 🎉";
        }
        await ReviewAsync(user.GuildId, user.Id, $"<@{user.Id}> answered “{Short(answer)}” to “{rules.Question}”.", rules);
        return "A moderator will look at your answer and let you in.";
    }

    // Lets a held member in: out of the waiting room, with the verified role. Who did it, if a moderator.
    public async Task<string> LetInAsync(ulong guildId, ulong userId, GuildUser? moderator)
    {
        var rules = await RulesAsync(guildId);
        await using var db = await dbFactory.CreateDbContextAsync();
        var hold = await db.GateHolds.FindAsync(guildId, userId);
        try
        {
            if (rules.VerifiedRoleId is { } verified)
                await rest.AddGuildUserRoleAsync(guildId, userId, verified);
            if (rules.WaitingRoleId is { } waiting)
                await rest.RemoveGuildUserRoleAsync(guildId, userId, waiting);
        }
        catch (RestException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden)
        {
            return ex.StatusCode == HttpStatusCode.NotFound ? "They aren't in the server any more." : "Discord won't let me change their roles: my role has to be above the waiting and verified roles.";
        }
        if (hold is not null)
        {
            db.GateHolds.Remove(hold);
            await db.SaveChangesAsync();
            await SettleReviewAsync(hold, moderator is null ? "✅ Verified" : $"✅ Let in by {moderator.Username}");
        }
        return $"<@{userId}> is in.";
    }

    public async Task<string> KickAsync(ulong guildId, ulong userId, GuildUser moderator)
    {
        var result = await actions.KickAsync(new(guildId, moderator.Id, moderator.Username), userId, "turned away at the gate");
        await using var db = await dbFactory.CreateDbContextAsync();
        if (await db.GateHolds.FindAsync(guildId, userId) is { } hold)
        {
            db.GateHolds.Remove(hold);
            await db.SaveChangesAsync();
            await SettleReviewAsync(hold, $"👢 Kicked by {moderator.Username}");
        }
        return result;
    }

    public async Task<GateRaid?> RaidAsync(ulong guildId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.GateRaids.AsNoTracking().FirstOrDefaultAsync(r => r.GuildId == guildId);
    }

    // Raid mode: the alert, Discord's own tools, and the raid's action on those who just joined.
    public async Task<string> StartRaidAsync(ulong guildId, GuildUser? by, string why, IReadOnlyList<ulong> joiners)
    {
        var rules = await RulesAsync(guildId);
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            if (await db.GateRaids.FindAsync(guildId) is not null)
                return "Raid mode is already on.";
            var raid = new GateRaid { GuildId = guildId, StartedAt = time.GetUtcNow(), LastJoinAt = time.GetUtcNow() };
            if (rules.RaiseVerification && gateway.Cache.Guilds.TryGetValue(guildId, out var guild) && guild.VerificationLevel < VerificationLevel.High)
            {
                try
                {
                    await rest.ModifyGuildAsync(guildId, g => g.VerificationLevel = VerificationLevel.High);
                    raid.PreviousVerificationLevel = (int)guild.VerificationLevel;
                }
                catch (RestException ex)
                {
                    logger.LogWarning("Raising the verification level in {GuildId} failed: {Message}", guildId, ex.Message);
                }
            }
            if (rules.PauseInvites)
                raid.InvitesPaused = await PauseInvitesAsync(guildId, true);
            db.GateRaids.Add(raid);
            await db.SaveChangesAsync();
        }
        var ping = rules.RaidAlertRoleId is { } role ? $"<@&{role}> " : "";
        await PostAsync(guildId, rules, $"{ping}🚨 **Raid mode is on**{(by is null ? "" : $", turned on by <@{by.Id}>")}: {why}. Newcomers are {Doing(rules.RaidAction)}"
            + $"{(rules.PauseInvites ? ", invites are paused" : "")}{(rules.RaiseVerification ? ", the verification level is raised" : "")}. "
            + $"It ends after {rules.RaidQuietMinutes} quiet minutes, or with `/mod raid off`.", rules.RaidAlertRoleId);
        foreach (var joiner in joiners)
            await ApplyAsync(guildId, joiner, HoldReasons.Raid, rules.RaidAction, rules);
        return "Raid mode is on.";
    }

    public async Task<string> EndRaidAsync(ulong guildId, GuildUser? by)
    {
        var rules = await RulesAsync(guildId);
        List<GateHold> held;
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            if (await db.GateRaids.FindAsync(guildId) is not { } raid)
                return "Raid mode isn't on.";
            if (raid.PreviousVerificationLevel is { } level)
            {
                try
                {
                    await rest.ModifyGuildAsync(guildId, g => g.VerificationLevel = (VerificationLevel)level);
                }
                catch (RestException ex)
                {
                    logger.LogWarning("Setting the verification level back in {GuildId} failed: {Message}", guildId, ex.Message);
                }
            }
            if (raid.InvitesPaused)
                await PauseInvitesAsync(guildId, false);
            db.GateRaids.Remove(raid);
            await db.SaveChangesAsync();
            held = await db.GateHolds.Where(h => h.GuildId == guildId && h.Reason == HoldReasons.Raid).ToListAsync();
        }
        if (rules.LetInAfterRaid)
        {
            foreach (var hold in held)
                await LetInAsync(guildId, hold.UserId, null);
        }
        await PostAsync(guildId, rules, $"✅ **Raid mode is off**{(by is null ? " (it went quiet)" : $", turned off by <@{by.Id}>")}."
            + (held.Count == 0 ? "" : rules.LetInAfterRaid ? $" {held.Count} held were let in." : $" {held.Count} still wait in the waiting room: let them in here, or they verify."));
        return "Raid mode is off.";
    }

    // Ends raids that went quiet.
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30), time);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var db = await dbFactory.CreateDbContextAsync(stoppingToken);
                foreach (var raid in await db.GateRaids.AsNoTracking().ToListAsync(stoppingToken))
                {
                    if (time.GetUtcNow() - raid.LastJoinAt >= TimeSpan.FromMinutes((await RulesAsync(raid.GuildId)).RaidQuietMinutes))
                        await EndRaidAsync(raid.GuildId, null);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Checking raids failed");
            }
        }
    }

    // Makes the waiting role (it sees only the waiting channel) and the channel, where they're missing.
    public async Task<string> PrepareAsync(Guild guild, ulong? channelId, ulong actorId)
    {
        var rules = await RulesAsync(guild.Id);
        var roleId = rules.WaitingRoleId is { } known && guild.Roles.ContainsKey(known) ? known
            : (await rest.CreateGuildRoleAsync(guild.Id, new() { Name = "Waiting", Permissions = 0 })).Id;
        var waitingId = channelId ?? (rules.WaitingChannelId is { } kept && guild.Channels.ContainsKey(kept) ? kept
            : (await rest.CreateGuildChannelAsync(guild.Id, new("waiting-room", ChannelType.TextGuildChannel))).Id);

        // Hidden from everything but the waiting channel, categories included (so their new channels are too).
        var hide = new PermissionOverwriteProperties(roleId, PermissionOverwriteType.Role) { Denied = Permissions.ViewChannel };
        var failed = 0;
        foreach (var channel in guild.Channels.Values.Where(c => c.Id != waitingId))
        {
            try
            {
                await rest.ModifyGuildChannelPermissionsAsync(channel.Id, hide);
            }
            catch (RestException)
            {
                failed++;
            }
        }
        await rest.ModifyGuildChannelPermissionsAsync(waitingId, new(roleId, PermissionOverwriteType.Role)
        {
            Allowed = Permissions.ViewChannel | Permissions.ReadMessageHistory,
            Denied = Permissions.SendMessages | Permissions.AddReactions,
        });
        // Everyone else doesn't need the waiting room; the bot still posts there.
        await rest.ModifyGuildChannelPermissionsAsync(waitingId, new(gateway.Cache.User!.Id, PermissionOverwriteType.User)
        {
            Allowed = Permissions.ViewChannel | Permissions.SendMessages | Permissions.ReadMessageHistory,
        });
        await rest.ModifyGuildChannelPermissionsAsync(waitingId, new(guild.Id, PermissionOverwriteType.Role) { Denied = Permissions.ViewChannel });

        await settings.SetAsync(guild.Id, ModuleId, rules with { WaitingRoleId = roleId, WaitingChannelId = waitingId }, actorId, "prepared the waiting room");
        await PostVerifyAsync(guild.Id);
        return $"The waiting room is <#{waitingId}> and the waiting role <@&{roleId}>; the role sees nothing else{(failed > 0 ? $" ({failed} channels I couldn't change)" : "")}."
            + " New channels outside a category need it hidden by hand. The rules and button are posted there.";
    }

    // The rules and the Verify button, in the waiting channel.
    public async Task<string> PostVerifyAsync(ulong guildId)
    {
        var rules = await RulesAsync(guildId);
        if (rules.WaitingChannelId is not { } channelId)
            return "There's no waiting channel yet: `/setup gate prepare` makes one.";
        var label = rules.Verify switch { VerifyModes.Question => "Answer to come in", VerifyModes.Mods => "Ask to come in", _ => "I agree, let me in" };
        await rest.SendMessageAsync(channelId, new()
        {
            Content = rules.Rules,
            Components = [new ActionRowProperties { new ButtonProperties(VerifyButton, label, EmojiProperties.Standard("✅"), ButtonStyle.Success) }],
        });
        return $"Posted in <#{channelId}>.";
    }

    private async Task ApplyAsync(ulong guildId, ulong userId, string reason, string action, GateRules rules)
    {
        var why = reason == HoldReasons.Raid ? "joined during a raid" : "a new Discord account";
        var bot = new Actor(guildId, gateway.Cache.User!.Id, "Gate");
        switch (action)
        {
            case GateActions.Timeout:
                await actions.TimeoutAsync(bot, userId, TimeSpan.FromMinutes(rules.TimeoutMinutes), why);
                break;
            case GateActions.Kick:
                await actions.KickAsync(bot, userId, why);
                break;
            case GateActions.Alert:
                await PostAsync(guildId, rules, $"👀 <@{userId}> joined: {why}.");
                break;
            default:
                await HoldAsync(guildId, userId, reason, rules);
                break;
        }
    }

    private async Task HoldAsync(ulong guildId, ulong userId, string reason, GateRules rules)
    {
        if (rules.WaitingRoleId is not { } waiting)
        {
            await PostAsync(guildId, rules, $"⚠️ <@{userId}> should wait in the waiting room, but there's none: `/setup gate prepare` makes one.");
            return;
        }
        try
        {
            await rest.AddGuildUserRoleAsync(guildId, userId, waiting);
        }
        catch (RestException ex)
        {
            logger.LogWarning("Holding {UserId} in {GuildId} failed: {Message}", userId, guildId, ex.Message);
            return;
        }
        await using var db = await dbFactory.CreateDbContextAsync();
        var hold = await db.GateHolds.FindAsync(guildId, userId);
        if (hold is null)
            db.GateHolds.Add(hold = new() { GuildId = guildId, UserId = userId, Reason = reason, Since = time.GetUtcNow() });
        await db.SaveChangesAsync();
        // Moderators decide: when they must, and for anyone held rather than verifying.
        if (reason != HoldReasons.Verify || rules.Verify == VerifyModes.Mods)
        {
            var why = reason switch { HoldReasons.Raid => "joined during a raid", HoldReasons.Young => "has a new Discord account", _ => "wants to come in" };
            if (await ReviewAsync(guildId, userId, $"⏳ <@{userId}> {why}.", rules) is { } posted)
            {
                (hold.ReviewChannelId, hold.ReviewMessageId) = posted;
                await db.SaveChangesAsync();
            }
        }
    }

    // A post in the gate channel with Let in / Kick buttons.
    private async Task<(ulong Channel, ulong Message)?> ReviewAsync(ulong guildId, ulong userId, string text, GateRules rules)
    {
        if (rules.ReviewChannelId is not { } channelId)
            return null;
        try
        {
            var message = await rest.SendMessageAsync(channelId, new()
            {
                Content = text,
                Components = [new ActionRowProperties
                {
                    new ButtonProperties($"gatelet:{userId}", "Let in", EmojiProperties.Standard("✅"), ButtonStyle.Success),
                    new ButtonProperties($"gatekick:{userId}", "Kick", EmojiProperties.Standard("👢"), ButtonStyle.Danger),
                }],
                AllowedMentions = AllowedMentionsProperties.None,
            });
            return (channelId, message.Id);
        }
        catch (RestException ex)
        {
            logger.LogWarning("Posting to the gate channel in {GuildId} failed: {Message}", guildId, ex.Message);
            return null;
        }
    }

    private async Task SettleReviewAsync(GateHold hold, string outcome)
    {
        if (hold is not { ReviewChannelId: { } channelId, ReviewMessageId: { } messageId })
            return;
        try
        {
            await rest.ModifyMessageAsync(channelId, messageId, m => m.Components = [new ActionRowProperties
            {
                new ButtonProperties("gatedone", outcome, ButtonStyle.Secondary) { Disabled = true },
            }]);
        }
        catch (RestException)
        {
        }
    }

    private async Task PostAsync(ulong guildId, GateRules rules, string text, ulong? pingRole = null)
    {
        if (rules.ReviewChannelId is not { } channelId)
            return;
        try
        {
            await rest.SendMessageAsync(channelId, new()
            {
                Content = text,
                AllowedMentions = pingRole is { } role ? new() { AllowedRoles = [role], Everyone = false } : AllowedMentionsProperties.None,
            });
        }
        catch (RestException ex)
        {
            logger.LogWarning("Posting to the gate channel in {GuildId} failed: {Message}", guildId, ex.Message);
        }
    }

    // Discord's "pause invites" (it can't last past a day; a longer raid stays paused till it ends or a day passes).
    private Task<bool> PauseInvitesAsync(ulong guildId, bool pause) => discord.PauseInvitesAsync(guildId, pause ? time.GetUtcNow().AddHours(24) : null);

    private static string Doing(string action) => action switch
    {
        GateActions.Timeout => "timed out",
        GateActions.Kick => "kicked",
        GateActions.Alert => "let in (and listed here)",
        _ => "held in the waiting room",
    };

    private static string Short(string text) => text.Length <= 200 ? text : text[..200] + "…";
}
