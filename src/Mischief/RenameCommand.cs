
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

public sealed class RenameCommand(
    ModuleState modules,
    SettingsStore settings,
    PointsEngine points,
    PaintRoles paints,
    Notifier notifier,
    IDbContextFactory<BotDbContext> dbFactory,
    TimeProvider time) : MischiefModule(modules, settings, points, paints, notifier, dbFactory, time)
{
    // Anyone may rename anyone, whatever their rank; renaming yourself costs a premium.
    [SlashCommand("rename", "Rename someone (yourself costs extra)", Contexts = [InteractionContextType.Guild])]
    public async Task RenameAsync(
        [SlashCommandParameter(Description = "Who to rename")] GuildUser user,
        [SlashCommandParameter(Description = "New nickname (leave out to reset it)", MaxLength = 32)] string? name = null,
        [SlashCommandParameter(Description = "Why", MaxLength = 200)] string? reason = null)
    {
        await DeferAsync();
        await FinishAsync(await Run());

        async Task<InteractionMessageProperties> Run()
        {
            if (await ModuleOffAsync() is { } off)
                return off;
            var self = user.Id == ActorId;
            var rules = await RulesAsync();
            var now = Time.GetUtcNow();
            await using var db = await DbFactory.CreateDbContextAsync();
    
            if (!self && await MischiefEffects.ActiveAsync(db, Guild.Id, user.Id, MischiefEffectKinds.Shield, now) is { } shield)
                return Replies.Ephemeral($"<@{user.Id}> is shielded until <t:{shield.EndsAt.ToUnixTimeSeconds()}:t>.");
            if (await MischiefEffects.ActiveAsync(db, Guild.Id, user.Id, MischiefEffectKinds.Lock, now) is { } nameLock)
                return Replies.Ephemeral($"<@{user.Id}>'s name is locked until <t:{nameLock.EndsAt.ToUnixTimeSeconds()}:t>. `/name unlock` breaks it for {Format(rules.LockBreakPrice(nameLock, now))}.");
    
            var history = await RenameHistory.ForTargetAsync(db, Guild.Id, user.Id, now - TimeSpan.FromHours(rules.RenameWindowHours));
            if (CooldownReply(rules, history, user.Id, now) is { } cooling)
                return cooling;
    
            var price = await PriceAsync(self ? rules.SelfRenamePrice() : rules.RenamePrice(history.RecentCount));
            if (await PayAsync(price, $"rename {user.Id}", self ? "Renaming yourself" : $"Renaming <@{user.Id}>") is { } unpaid)
                return unpaid;
    
            name = Clean(name);
            reason = Clean(reason);
            if (!await SetNicknameAsync(user, name))
            {
                await RefundAsync(price, $"rename {user.Id} refused by Discord");
                return Replies.Ephemeral($"Discord won't let me rename <@{user.Id}>: bots can't rename the owner, or anyone whose top role is above the bot's.");
            }
    
            await RecordRenameAsync(db, user, name, reason, price, now);
    
            if (!self)
                await Notifier.NotifyAsync(Guild.Id, NotificationTopics.MischiefYou, [user.Id], $"{Context.User.Username} renamed you to **{name ?? "(no nickname)"}**{(reason is null ? "" : $": {reason}")}", Notifier.Link(Guild.Id, Context.Channel.Id));
            var who = self ? "themselves" : $"<@{user.Id}>";
            var what = name is null ? $"reset {(self ? "their own" : $"<@{user.Id}>'s")} nickname" : $"renamed {who} to **{name}**";
            return Public($"<@{ActorId}> {what}{(reason is null ? "" : $": {reason}")}{Paid(price)}");
        }
    }
}
