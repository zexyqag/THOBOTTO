
using Microsoft.EntityFrameworkCore;

using NetCord;
using NetCord.Gateway;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;

using THOBOTTO.Data;
using THOBOTTO.Modules;
using THOBOTTO.Notifications;
using THOBOTTO.Points;

namespace THOBOTTO.Mischief;

[SlashCommand("name", "Names: shields, locks, colours, history, prices", Contexts = [InteractionContextType.Guild])]
public sealed partial class NameCommands(
    ModuleState modules,
    SettingsStore settings,
    PointsEngine points,
    PaintRoles paints,
    Notifier notifier,
    IDbContextFactory<BotDbContext> dbFactory,
    TimeProvider time) : MischiefModule(modules, settings, points, paints, notifier, dbFactory, time)
{
    [SubSlashCommand("shield", "Nobody can rename or paint you for a while")]
    public async Task<InteractionMessageProperties> ShieldAsync(
        [SlashCommandParameter(Description = "How many hours", MinValue = 1, MaxValue = 168)] int hours)
    {
        if (await ModuleOffAsync() is { } off)
            return off;

        var rules = await RulesAsync();
        if (hours > rules.ShieldMaxHours)
            return Replies.Ephemeral($"Shields last at most {rules.ShieldMaxHours} hours.");

        var now = Time.GetUtcNow();
        await using var db = await DbFactory.CreateDbContextAsync();

        // An active shield is extended from where it ends.
        var current = await MischiefEffects.ActiveAsync(db, Guild.Id, ActorId, MischiefEffectKinds.Shield, now);
        var start = current?.EndsAt ?? now;
        if (start + TimeSpan.FromHours(hours) > now + TimeSpan.FromHours(rules.ShieldMaxHours))
            return Replies.Ephemeral($"That would shield you past {rules.ShieldMaxHours} hours from now; your shield already runs until <t:{start.ToUnixTimeSeconds()}:t>.");

        var price = await PriceAsync(rules.ShieldPrice(hours));
        if (await PayAsync(price, $"shield {hours}h", $"A {hours} h shield") is { } unpaid)
            return unpaid;

        var shield = AddEffect(db, MischiefEffectKinds.Shield, ActorId, price, now, start + TimeSpan.FromHours(hours));
        await db.SaveChangesAsync();

        return Public($"<@{ActorId}> is shielded from mischief until <t:{shield.EndsAt.ToUnixTimeSeconds()}:t>{Paid(price)}.");
    }

    [SubSlashCommand("lock", "Keep someone's current name for a while")]
    public async Task LockAsync(
        [SlashCommandParameter(Description = "Whose name to lock")] GuildUser user,
        [SlashCommandParameter(Description = "How many hours", MinValue = 1, MaxValue = 168)] int hours)
    {
        await DeferAsync();
        await FinishAsync(await Run());

        async Task<InteractionMessageProperties> Run()
        {
            if (await ModuleOffAsync() is { } off)
                return off;
            if (user.Id == ActorId)
                return Replies.Ephemeral("You can't lock your own name. A `/name shield` keeps others from renaming you.");
    
            var rules = await RulesAsync();
            if (hours > rules.LockMaxHours)
                return Replies.Ephemeral($"Locks last at most {rules.LockMaxHours} hours.");
    
            var now = Time.GetUtcNow();
            await using var db = await DbFactory.CreateDbContextAsync();
            if (await MischiefEffects.ActiveAsync(db, Guild.Id, user.Id, MischiefEffectKinds.Lock, now) is { } existing)
                return Replies.Ephemeral($"<@{user.Id}>'s name is already locked until <t:{existing.EndsAt.ToUnixTimeSeconds()}:t>.");
    
            var price = await PriceAsync(rules.LockPrice(hours));
            if (await PayAsync(price, $"lock {user.Id} {hours}h", $"Locking <@{user.Id}>'s name for {hours} h") is { } unpaid)
                return unpaid;
    
            var nameLock = AddEffect(db, MischiefEffectKinds.Lock, user.Id, price, now, now + TimeSpan.FromHours(hours));
            await db.SaveChangesAsync();
            await Notifier.NotifyAsync(Guild.Id, NotificationTopics.MischiefYou, [user.Id], $"{Context.User.Username} locked your name for {hours} h", Notifier.Link(Guild.Id, Context.Channel.Id));
    
            var name = user.Nickname ?? user.GlobalName ?? user.Username;
            return Public($"<@{ActorId}> locked <@{user.Id}>'s name as **{name}** until <t:{nameLock.EndsAt.ToUnixTimeSeconds()}:t>{Paid(price)}.");
        }
    }

    [SubSlashCommand("unlock", "Break a name lock (costs more than the lock did)")]
    public async Task<InteractionMessageProperties> UnlockAsync(
        [SlashCommandParameter(Description = "Whose name to unlock (you if left out)")] GuildUser? user = null)
    {
        if (await ModuleOffAsync() is { } off)
            return off;

        var targetId = user?.Id ?? ActorId;
        var rules = await RulesAsync();
        var now = Time.GetUtcNow();
        await using var db = await DbFactory.CreateDbContextAsync();

        if (await MischiefEffects.ActiveAsync(db, Guild.Id, targetId, MischiefEffectKinds.Lock, now) is not { } nameLock)
            return Replies.Ephemeral($"<@{targetId}>'s name isn't locked.");

        var price = await PriceAsync(rules.LockBreakPrice(nameLock, now));
        if (await PayAsync(price, $"unlock {targetId}", $"Breaking <@{targetId}>'s lock") is { } unpaid)
            return unpaid;

        nameLock.EndsAt = now;
        nameLock.EndedById = ActorId;
        await db.SaveChangesAsync();

        return Public($"<@{ActorId}> broke the lock on <@{targetId}>'s name{Paid(price)}.");
    }

    [SubSlashCommand("buyback", "Buy your own name back (resets your nickname)")]
    public async Task BuyBackAsync()
    {
        await DeferAsync();
        await FinishAsync(await Run());

        async Task<InteractionMessageProperties> Run()
        {
            if (await ModuleOffAsync() is { } off)
                return off;
    
            var self = (GuildUser)Context.User;
            if (self.Nickname is null)
                return Replies.Ephemeral("You don't have a nickname to get rid of.");
    
            var rules = await RulesAsync();
            var now = Time.GetUtcNow();
            await using var db = await DbFactory.CreateDbContextAsync();
    
            if (await MischiefEffects.ActiveAsync(db, Guild.Id, self.Id, MischiefEffectKinds.Lock, now) is { } nameLock)
                return Replies.Ephemeral($"Your name is locked until <t:{nameLock.EndsAt.ToUnixTimeSeconds()}:t>. `/name unlock` breaks it for {Format(rules.LockBreakPrice(nameLock, now))}.");
    
            var history = await RenameHistory.ForTargetAsync(db, Guild.Id, self.Id, now - TimeSpan.FromHours(rules.RenameWindowHours));
            var since = history.LastByOthersAt is { } last ? now - last : TimeSpan.MaxValue;
            var price = await PriceAsync(rules.BuyBackPrice(since));
            if (await PayAsync(price, "buyback", "Buying your name back") is { } unpaid)
                return unpaid;
    
            if (!await SetNicknameAsync(self, null))
            {
                await RefundAsync(price, "buyback refused by Discord");
                return Replies.Ephemeral("Discord won't let me change your nickname: bots can't rename the owner, or anyone whose top role is above the bot's.");
            }
    
            await RecordRenameAsync(db, self, null, "bought back", price, now);
            return Public($"<@{ActorId}> bought their name back{Paid(price)}.");
        }
    }

    [SubSlashCommand("colour", "Give someone a name colour for a while")]
    public async Task PaintAsync(
        [SlashCommandParameter(Description = "Who to paint")] GuildUser user,
        [SlashCommandParameter(Description = "A colour name or a hex code like #ff00ff", AutocompleteProviderType = typeof(PaintColourAutocomplete))] string colour,
        [SlashCommandParameter(Description = "How many hours", MinValue = 1, MaxValue = 168)] int hours)
    {
        await DeferAsync();
        await FinishAsync(await Run());

        async Task<InteractionMessageProperties> Run()
        {
            if (await ModuleOffAsync() is { } off)
                return off;
            if (!PaintColours.TryParse(colour, out var rgb, out var colourName))
                return Replies.Ephemeral("That isn't a colour I know. Pick one from the list or use a hex code like `#ff00ff`.");
    
            var rules = await RulesAsync();
            if (hours > rules.PaintMaxHours)
                return Replies.Ephemeral($"Paint lasts at most {rules.PaintMaxHours} hours.");
    
            var now = Time.GetUtcNow();
            await using var db = await DbFactory.CreateDbContextAsync();
            var self = user.Id == ActorId;
            if (!self && await MischiefEffects.ActiveAsync(db, Guild.Id, user.Id, MischiefEffectKinds.Shield, now) is { } shield)
                return Replies.Ephemeral($"<@{user.Id}> is shielded until <t:{shield.EndsAt.ToUnixTimeSeconds()}:t>.");
    
            var price = await PriceAsync(self ? rules.SelfPaintPrice(hours) : rules.PaintPrice(hours));
            if (await PayAsync(price, $"paint {user.Id} {colourName} {hours}h", self ? $"Painting yourself for {hours} h" : $"Painting <@{user.Id}> for {hours} h") is { } unpaid)
                return unpaid;
    
            if (await Paints.ApplyAsync(Guild.Id, user.Id, rgb, colourName) is not { } roleId)
            {
                await RefundAsync(price, $"paint {user.Id} refused by Discord");
                return Replies.Ephemeral("Discord won't let me do that: I need Manage Roles, and my role has to be above the member's top role.");
            }
    
            // A new coat replaces the old one.
            if (await MischiefEffects.ActiveAsync(db, Guild.Id, user.Id, MischiefEffectKinds.Paint, now) is { } old)
            {
                await Paints.RemoveAsync(old);
                old.EndsAt = now;
                old.EndedById = ActorId;
            }
    
            var paint = AddEffect(db, MischiefEffectKinds.Paint, user.Id, price, now, now + TimeSpan.FromHours(hours));
            paint.RoleId = roleId;
            await db.SaveChangesAsync();
            if (!self)
                await Notifier.NotifyAsync(Guild.Id, NotificationTopics.MischiefYou, [user.Id], $"{Context.User.Username} painted you {colourName} for {hours} h", Notifier.Link(Guild.Id, Context.Channel.Id));
    
            return Public($"<@{ActorId}> painted {(self ? "themselves" : $"<@{user.Id}>")} **{colourName}** until <t:{paint.EndsAt.ToUnixTimeSeconds()}:t>{Paid(price)}.");
        }
    }

    [SubSlashCommand("uncolour", "Remove a name colour early (costs more than colouring did)")]
    public async Task UnpaintAsync(
        [SlashCommandParameter(Description = "Who to clean up (you if left out)")] GuildUser? user = null)
    {
        await DeferAsync();
        await FinishAsync(await Run());

        async Task<InteractionMessageProperties> Run()
        {
            if (await ModuleOffAsync() is { } off)
                return off;
    
            var targetId = user?.Id ?? ActorId;
            var rules = await RulesAsync();
            var now = Time.GetUtcNow();
            await using var db = await DbFactory.CreateDbContextAsync();
    
            if (await MischiefEffects.ActiveAsync(db, Guild.Id, targetId, MischiefEffectKinds.Paint, now) is not { } paint)
                return Replies.Ephemeral($"<@{targetId}> isn't painted.");
    
            var price = await PriceAsync(rules.PaintBreakPrice(paint, now));
            if (await PayAsync(price, $"unpaint {targetId}", $"Removing <@{targetId}>'s paint") is { } unpaid)
                return unpaid;
    
            await Paints.RemoveAsync(paint);
            paint.EndsAt = now;
            paint.EndedById = ActorId;
            await db.SaveChangesAsync();
    
            return Public($"<@{ActorId}> washed the paint off <@{targetId}>{Paid(price)}.");
        }
    }
}
