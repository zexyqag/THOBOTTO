using System.Net;

using Microsoft.EntityFrameworkCore;

using NetCord;
using NetCord.Rest;

using THOBOTTO.Data;
using THOBOTTO.Modules;
using THOBOTTO.Notifications;
using THOBOTTO.Points;

namespace THOBOTTO.Mischief;

// Who does it, and the channel it's done from (for the links in notifications).
public sealed record MischiefActor(ulong GuildId, ulong UserId, string Name, ulong ChannelId);

// The answer: announced in the channel, or only for the one who asked (refusals).
public sealed record MischiefResult(string Text, bool Public)
{
    public static MischiefResult Private(string text) => new(text, false);
}

// Renames, shields, locks, paint and buying your name back: paid for, checked against shields, locks and
// cooldowns, and recorded. The slash commands and voice commands both come here.
public sealed class MischiefActions(
    ModuleState modules,
    SettingsStore settings,
    PointsEngine points,
    PaintRoles paints,
    Notifier notifier,
    IDbContextFactory<BotDbContext> dbFactory,
    TimeProvider time)
{
    public async Task<MischiefResult?> OffAsync(ulong guildId)
        => await modules.IsEnabledAsync(guildId, MischiefModule.ModuleId) ? null : MischiefResult.Private($"The `{MischiefModule.ModuleId}` module is off.");

    // Anyone may rename anyone, whatever their rank; renaming yourself costs a premium.
    public async Task<MischiefResult> RenameAsync(MischiefActor actor, GuildUser user, string? name, string? reason)
    {
        if (await OffAsync(actor.GuildId) is { } off)
            return off;
        var self = user.Id == actor.UserId;
        var rules = await RulesAsync(actor);
        var now = time.GetUtcNow();
        await using var db = await dbFactory.CreateDbContextAsync();

        if (!self && await MischiefEffects.ActiveAsync(db, actor.GuildId, user.Id, MischiefEffectKinds.Shield, now) is { } shield)
            return MischiefResult.Private($"<@{user.Id}> is shielded until <t:{shield.EndsAt.ToUnixTimeSeconds()}:t>.");
        if (await MischiefEffects.ActiveAsync(db, actor.GuildId, user.Id, MischiefEffectKinds.Lock, now) is { } nameLock)
            return MischiefResult.Private($"<@{user.Id}>'s name is locked until <t:{nameLock.EndsAt.ToUnixTimeSeconds()}:t>. `/name unlock` breaks it for {Format(actor, rules.LockBreakPrice(nameLock, now))}.");

        var history = await RenameHistory.ForTargetAsync(db, actor.GuildId, user.Id, now - TimeSpan.FromHours(rules.RenameWindowHours));
        if (Cooldown(rules, history, user.Id, now) is { } cooling)
            return cooling;

        var price = await PriceAsync(actor, self ? rules.SelfRenamePrice() : rules.RenamePrice(history.RecentCount));
        if (await PayAsync(actor, price, $"rename {user.Id}", self ? "Renaming yourself" : $"Renaming <@{user.Id}>") is { } unpaid)
            return unpaid;

        name = Clean(name);
        reason = Clean(reason);
        if (!await SetNicknameAsync(user, name))
        {
            await RefundAsync(actor, price, $"rename {user.Id} refused by Discord");
            return MischiefResult.Private($"Discord won't let me rename <@{user.Id}>: bots can't rename the owner, or anyone whose top role is above the bot's.");
        }

        await RecordRenameAsync(db, actor, user, name, reason, price, now);

        if (!self)
            await notifier.NotifyAsync(actor.GuildId, NotificationTopics.MischiefYou, [user.Id], $"{actor.Name} renamed you to **{name ?? "(no nickname)"}**{(reason is null ? "" : $": {reason}")}", Notifier.Link(actor.GuildId, actor.ChannelId));
        var who = self ? "themselves" : $"<@{user.Id}>";
        var what = name is null ? $"reset {(self ? "their own" : $"<@{user.Id}>'s")} nickname" : $"renamed {who} to **{name}**";
        return new($"<@{actor.UserId}> {what}{(reason is null ? "" : $": {reason}")}{Paid(actor, price)}", true);
    }

    public async Task<MischiefResult> ShieldAsync(MischiefActor actor, int hours)
    {
        if (await OffAsync(actor.GuildId) is { } off)
            return off;
        var rules = await RulesAsync(actor);
        if (hours > rules.ShieldMaxHours)
            return MischiefResult.Private($"Shields last at most {rules.ShieldMaxHours} hours.");

        var now = time.GetUtcNow();
        await using var db = await dbFactory.CreateDbContextAsync();

        // An active shield is extended from where it ends.
        var current = await MischiefEffects.ActiveAsync(db, actor.GuildId, actor.UserId, MischiefEffectKinds.Shield, now);
        var start = current?.EndsAt ?? now;
        if (start + TimeSpan.FromHours(hours) > now + TimeSpan.FromHours(rules.ShieldMaxHours))
            return MischiefResult.Private($"That would shield you past {rules.ShieldMaxHours} hours from now; your shield already runs until <t:{start.ToUnixTimeSeconds()}:t>.");

        var price = await PriceAsync(actor, rules.ShieldPrice(hours));
        if (await PayAsync(actor, price, $"shield {hours}h", $"A {hours} h shield") is { } unpaid)
            return unpaid;

        var shield = AddEffect(db, actor, MischiefEffectKinds.Shield, actor.UserId, price, now, start + TimeSpan.FromHours(hours));
        await db.SaveChangesAsync();
        return new($"<@{actor.UserId}> is shielded from mischief until <t:{shield.EndsAt.ToUnixTimeSeconds()}:t>{Paid(actor, price)}.", true);
    }

    public async Task<MischiefResult> LockAsync(MischiefActor actor, GuildUser user, int hours)
    {
        if (await OffAsync(actor.GuildId) is { } off)
            return off;
        if (user.Id == actor.UserId)
            return MischiefResult.Private("You can't lock your own name. A `/name shield` keeps others from renaming you.");

        var rules = await RulesAsync(actor);
        if (hours > rules.LockMaxHours)
            return MischiefResult.Private($"Locks last at most {rules.LockMaxHours} hours.");

        var now = time.GetUtcNow();
        await using var db = await dbFactory.CreateDbContextAsync();
        if (await MischiefEffects.ActiveAsync(db, actor.GuildId, user.Id, MischiefEffectKinds.Lock, now) is { } existing)
            return MischiefResult.Private($"<@{user.Id}>'s name is already locked until <t:{existing.EndsAt.ToUnixTimeSeconds()}:t>.");

        var price = await PriceAsync(actor, rules.LockPrice(hours));
        if (await PayAsync(actor, price, $"lock {user.Id} {hours}h", $"Locking <@{user.Id}>'s name for {hours} h") is { } unpaid)
            return unpaid;

        var nameLock = AddEffect(db, actor, MischiefEffectKinds.Lock, user.Id, price, now, now + TimeSpan.FromHours(hours));
        await db.SaveChangesAsync();
        await notifier.NotifyAsync(actor.GuildId, NotificationTopics.MischiefYou, [user.Id], $"{actor.Name} locked your name for {hours} h", Notifier.Link(actor.GuildId, actor.ChannelId));

        var name = user.Nickname ?? user.GlobalName ?? user.Username;
        return new($"<@{actor.UserId}> locked <@{user.Id}>'s name as **{name}** until <t:{nameLock.EndsAt.ToUnixTimeSeconds()}:t>{Paid(actor, price)}.", true);
    }

    public async Task<MischiefResult> UnlockAsync(MischiefActor actor, ulong targetId)
    {
        if (await OffAsync(actor.GuildId) is { } off)
            return off;
        var rules = await RulesAsync(actor);
        var now = time.GetUtcNow();
        await using var db = await dbFactory.CreateDbContextAsync();

        if (await MischiefEffects.ActiveAsync(db, actor.GuildId, targetId, MischiefEffectKinds.Lock, now) is not { } nameLock)
            return MischiefResult.Private($"<@{targetId}>'s name isn't locked.");

        var price = await PriceAsync(actor, rules.LockBreakPrice(nameLock, now));
        if (await PayAsync(actor, price, $"unlock {targetId}", $"Breaking <@{targetId}>'s lock") is { } unpaid)
            return unpaid;

        nameLock.EndsAt = now;
        nameLock.EndedById = actor.UserId;
        await db.SaveChangesAsync();
        return new($"<@{actor.UserId}> broke the lock on <@{targetId}>'s name{Paid(actor, price)}.", true);
    }

    public async Task<MischiefResult> BuyBackAsync(MischiefActor actor, GuildUser self)
    {
        if (await OffAsync(actor.GuildId) is { } off)
            return off;
        if (self.Nickname is null)
            return MischiefResult.Private("You don't have a nickname to get rid of.");

        var rules = await RulesAsync(actor);
        var now = time.GetUtcNow();
        await using var db = await dbFactory.CreateDbContextAsync();

        if (await MischiefEffects.ActiveAsync(db, actor.GuildId, self.Id, MischiefEffectKinds.Lock, now) is { } nameLock)
            return MischiefResult.Private($"Your name is locked until <t:{nameLock.EndsAt.ToUnixTimeSeconds()}:t>. `/name unlock` breaks it for {Format(actor, rules.LockBreakPrice(nameLock, now))}.");

        var history = await RenameHistory.ForTargetAsync(db, actor.GuildId, self.Id, now - TimeSpan.FromHours(rules.RenameWindowHours));
        var since = history.LastByOthersAt is { } last ? now - last : TimeSpan.MaxValue;
        var price = await PriceAsync(actor, rules.BuyBackPrice(since));
        if (await PayAsync(actor, price, "buyback", "Buying your name back") is { } unpaid)
            return unpaid;

        if (!await SetNicknameAsync(self, null))
        {
            await RefundAsync(actor, price, "buyback refused by Discord");
            return MischiefResult.Private("Discord won't let me change your nickname: bots can't rename the owner, or anyone whose top role is above the bot's.");
        }

        await RecordRenameAsync(db, actor, self, null, "bought back", price, now);
        return new($"<@{actor.UserId}> bought their name back{Paid(actor, price)}.", true);
    }

    public async Task<MischiefResult> PaintAsync(MischiefActor actor, GuildUser user, string colour, int hours)
    {
        if (await OffAsync(actor.GuildId) is { } off)
            return off;
        if (!PaintColours.TryParse(colour, out var rgb, out var colourName))
            return MischiefResult.Private("That isn't a colour I know. Pick one from the list or use a hex code like `#ff00ff`.");

        var rules = await RulesAsync(actor);
        if (hours > rules.PaintMaxHours)
            return MischiefResult.Private($"Paint lasts at most {rules.PaintMaxHours} hours.");

        var now = time.GetUtcNow();
        await using var db = await dbFactory.CreateDbContextAsync();
        var self = user.Id == actor.UserId;
        if (!self && await MischiefEffects.ActiveAsync(db, actor.GuildId, user.Id, MischiefEffectKinds.Shield, now) is { } shield)
            return MischiefResult.Private($"<@{user.Id}> is shielded until <t:{shield.EndsAt.ToUnixTimeSeconds()}:t>.");

        var price = await PriceAsync(actor, self ? rules.SelfPaintPrice(hours) : rules.PaintPrice(hours));
        if (await PayAsync(actor, price, $"paint {user.Id} {colourName} {hours}h", self ? $"Painting yourself for {hours} h" : $"Painting <@{user.Id}> for {hours} h") is { } unpaid)
            return unpaid;

        if (await paints.ApplyAsync(actor.GuildId, user.Id, rgb, colourName) is not { } roleId)
        {
            await RefundAsync(actor, price, $"paint {user.Id} refused by Discord");
            return MischiefResult.Private("Discord won't let me do that: I need Manage Roles, and my role has to be above the member's top role.");
        }

        // A new coat replaces the old one.
        if (await MischiefEffects.ActiveAsync(db, actor.GuildId, user.Id, MischiefEffectKinds.Paint, now) is { } old)
        {
            await paints.RemoveAsync(old);
            old.EndsAt = now;
            old.EndedById = actor.UserId;
        }

        var paint = AddEffect(db, actor, MischiefEffectKinds.Paint, user.Id, price, now, now + TimeSpan.FromHours(hours));
        paint.RoleId = roleId;
        await db.SaveChangesAsync();
        if (!self)
            await notifier.NotifyAsync(actor.GuildId, NotificationTopics.MischiefYou, [user.Id], $"{actor.Name} painted you {colourName} for {hours} h", Notifier.Link(actor.GuildId, actor.ChannelId));

        return new($"<@{actor.UserId}> painted {(self ? "themselves" : $"<@{user.Id}>")} **{colourName}** until <t:{paint.EndsAt.ToUnixTimeSeconds()}:t>{Paid(actor, price)}.", true);
    }

    public async Task<MischiefResult> UnpaintAsync(MischiefActor actor, ulong targetId)
    {
        if (await OffAsync(actor.GuildId) is { } off)
            return off;
        var rules = await RulesAsync(actor);
        var now = time.GetUtcNow();
        await using var db = await dbFactory.CreateDbContextAsync();

        if (await MischiefEffects.ActiveAsync(db, actor.GuildId, targetId, MischiefEffectKinds.Paint, now) is not { } paint)
            return MischiefResult.Private($"<@{targetId}> isn't painted.");

        var price = await PriceAsync(actor, rules.PaintBreakPrice(paint, now));
        if (await PayAsync(actor, price, $"unpaint {targetId}", $"Removing <@{targetId}>'s paint") is { } unpaid)
            return unpaid;

        await paints.RemoveAsync(paint);
        paint.EndsAt = now;
        paint.EndedById = actor.UserId;
        await db.SaveChangesAsync();
        return new($"<@{actor.UserId}> washed the paint off <@{targetId}>{Paid(actor, price)}.", true);
    }

    private ValueTask<MischiefRules> RulesAsync(MischiefActor actor) => settings.GetAsync<MischiefRules>(actor.GuildId, MischiefModule.ModuleId);

    // Free while the points module is off.
    private async Task<double> PriceAsync(MischiefActor actor, double price) => await points.ChargesAsync(actor.GuildId) ? price : 0;

    private async Task<MischiefResult?> PayAsync(MischiefActor actor, double price, string ledgerReason, string what)
    {
        if (price <= 0 || await points.TrySpendAsync(actor.GuildId, actor.UserId, price, ledgerReason))
            return null;
        var balance = (await points.GetAsync(actor.GuildId, actor.UserId))?.Balance ?? 0;
        return MischiefResult.Private($"{what} costs {Format(actor, price)}; you have {Format(actor, balance)}.");
    }

    private async Task RefundAsync(MischiefActor actor, double price, string reason)
    {
        if (price > 0)
            await points.RefundAsync(actor.GuildId, actor.UserId, price, reason);
    }

    private string Format(MischiefActor actor, double amount) => points.Rules(actor.GuildId).Format(amount);

    private string Paid(MischiefActor actor, double price) => price > 0 ? $" ({Format(actor, price)})" : "";

    private static MischiefResult? Cooldown(MischiefRules rules, RenameHistory history, ulong userId, DateTimeOffset now)
    {
        if (history.LastAt is not { } last || now - last >= TimeSpan.FromMinutes(rules.RenameCooldownMinutes))
            return null;
        var until = last + TimeSpan.FromMinutes(rules.RenameCooldownMinutes);
        return MischiefResult.Private($"<@{userId}> was renamed <t:{last.ToUnixTimeSeconds()}:R>. They can be renamed again <t:{until.ToUnixTimeSeconds()}:R>.");
    }

    private static async Task<bool> SetNicknameAsync(GuildUser user, string? name)
    {
        try
        {
            await user.ModifyAsync(u => u.Nickname = name ?? "");
            return true;
        }
        catch (RestException ex) when (ex.StatusCode == HttpStatusCode.Forbidden)
        {
            return false;
        }
    }

    private static async Task RecordRenameAsync(BotDbContext db, MischiefActor actor, GuildUser target, string? name, string? reason, double price, DateTimeOffset now)
    {
        db.Renames.Add(new()
        {
            GuildId = actor.GuildId,
            ActorId = actor.UserId,
            TargetId = target.Id,
            OldName = target.Nickname,
            NewName = name,
            Reason = reason,
            Cost = price,
            CreatedAt = now,
        });
        await db.SaveChangesAsync();
    }

    private static MischiefEffect AddEffect(BotDbContext db, MischiefActor actor, string kind, ulong targetId, double paid, DateTimeOffset now, DateTimeOffset endsAt)
    {
        var effect = new MischiefEffect
        {
            GuildId = actor.GuildId,
            Kind = kind,
            TargetId = targetId,
            ActorId = actor.UserId,
            Paid = paid,
            CreatedAt = now,
            EndsAt = endsAt,
        };
        db.MischiefEffects.Add(effect);
        return effect;
    }

    private static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
