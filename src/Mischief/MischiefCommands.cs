using System.Net;

using Microsoft.EntityFrameworkCore;

using NetCord;
using NetCord.Gateway;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;

using THOBOTTO.Data;
using THOBOTTO.Modules;
using THOBOTTO.Points;

namespace THOBOTTO.Mischief;

public sealed class MischiefCommands(
    ModuleState modules,
    SettingsStore settings,
    PointsEngine points,
    PaintRoles paints,
    IDbContextFactory<BotDbContext> dbFactory,
    TimeProvider time) : ApplicationCommandModule<ApplicationCommandContext>
{
    public const string ModuleId = "mischief";

    private Guild Guild => Context.Guild!;

    private ulong ActorId => Context.User.Id;

    // Anyone may rename anyone, whatever their rank; renaming yourself costs a premium.
    [SlashCommand("rename", "Rename someone (yourself costs extra)", Contexts = [InteractionContextType.Guild])]
    public async Task<InteractionMessageProperties> RenameAsync(
        [SlashCommandParameter(Description = "Who to rename")] GuildUser user,
        [SlashCommandParameter(Description = "New nickname (leave out to reset it)", MaxLength = 32)] string? name = null,
        [SlashCommandParameter(Description = "Why", MaxLength = 200)] string? reason = null)
    {
        if (await ModuleOffAsync() is { } off)
            return off;
        var self = user.Id == ActorId;
        var rules = await RulesAsync();
        var now = time.GetUtcNow();
        await using var db = await dbFactory.CreateDbContextAsync();

        if (!self && await MischiefEffects.ActiveAsync(db, Guild.Id, user.Id, MischiefEffectKinds.Shield, now) is { } shield)
            return Replies.Ephemeral($"<@{user.Id}> is shielded until <t:{shield.EndsAt.ToUnixTimeSeconds()}:t>.");
        if (await MischiefEffects.ActiveAsync(db, Guild.Id, user.Id, MischiefEffectKinds.Lock, now) is { } nameLock)
            return Replies.Ephemeral($"<@{user.Id}>'s name is locked until <t:{nameLock.EndsAt.ToUnixTimeSeconds()}:t>. `/unlock` breaks it for {Format(rules.LockBreakPrice(nameLock, now))}.");

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

        var who = self ? "themselves" : $"<@{user.Id}>";
        var what = name is null ? $"reset {(self ? "their own" : $"<@{user.Id}>'s")} nickname" : $"renamed {who} to **{name}**";
        return Public($"<@{ActorId}> {what}{(reason is null ? "" : $": {reason}")}{Paid(price)}");
    }

    [SlashCommand("buyback", "Buy your own name back (resets your nickname)", Contexts = [InteractionContextType.Guild])]
    public async Task<InteractionMessageProperties> BuyBackAsync()
    {
        if (await ModuleOffAsync() is { } off)
            return off;

        var self = (GuildUser)Context.User;
        if (self.Nickname is null)
            return Replies.Ephemeral("You don't have a nickname to get rid of.");

        var rules = await RulesAsync();
        var now = time.GetUtcNow();
        await using var db = await dbFactory.CreateDbContextAsync();

        if (await MischiefEffects.ActiveAsync(db, Guild.Id, self.Id, MischiefEffectKinds.Lock, now) is { } nameLock)
            return Replies.Ephemeral($"Your name is locked until <t:{nameLock.EndsAt.ToUnixTimeSeconds()}:t>. `/unlock` breaks it for {Format(rules.LockBreakPrice(nameLock, now))}.");

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

    [SlashCommand("shield", "Nobody can rename or paint you for a while", Contexts = [InteractionContextType.Guild])]
    public async Task<InteractionMessageProperties> ShieldAsync(
        [SlashCommandParameter(Description = "How many hours", MinValue = 1, MaxValue = 168)] int hours)
    {
        if (await ModuleOffAsync() is { } off)
            return off;

        var rules = await RulesAsync();
        if (hours > rules.ShieldMaxHours)
            return Replies.Ephemeral($"Shields last at most {rules.ShieldMaxHours} hours.");

        var now = time.GetUtcNow();
        await using var db = await dbFactory.CreateDbContextAsync();

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

    [SlashCommand("lock", "Keep someone's current name for a while", Contexts = [InteractionContextType.Guild])]
    public async Task<InteractionMessageProperties> LockAsync(
        [SlashCommandParameter(Description = "Whose name to lock")] GuildUser user,
        [SlashCommandParameter(Description = "How many hours", MinValue = 1, MaxValue = 168)] int hours)
    {
        if (await ModuleOffAsync() is { } off)
            return off;
        if (user.Id == ActorId)
            return Replies.Ephemeral("You can't lock your own name. A `/shield` keeps others from renaming you.");

        var rules = await RulesAsync();
        if (hours > rules.LockMaxHours)
            return Replies.Ephemeral($"Locks last at most {rules.LockMaxHours} hours.");

        var now = time.GetUtcNow();
        await using var db = await dbFactory.CreateDbContextAsync();
        if (await MischiefEffects.ActiveAsync(db, Guild.Id, user.Id, MischiefEffectKinds.Lock, now) is { } existing)
            return Replies.Ephemeral($"<@{user.Id}>'s name is already locked until <t:{existing.EndsAt.ToUnixTimeSeconds()}:t>.");

        var price = await PriceAsync(rules.LockPrice(hours));
        if (await PayAsync(price, $"lock {user.Id} {hours}h", $"Locking <@{user.Id}>'s name for {hours} h") is { } unpaid)
            return unpaid;

        var nameLock = AddEffect(db, MischiefEffectKinds.Lock, user.Id, price, now, now + TimeSpan.FromHours(hours));
        await db.SaveChangesAsync();

        var name = user.Nickname ?? user.GlobalName ?? user.Username;
        return Public($"<@{ActorId}> locked <@{user.Id}>'s name as **{name}** until <t:{nameLock.EndsAt.ToUnixTimeSeconds()}:t>{Paid(price)}.");
    }

    [SlashCommand("unlock", "Break a name lock (costs more than the lock did)", Contexts = [InteractionContextType.Guild])]
    public async Task<InteractionMessageProperties> UnlockAsync(
        [SlashCommandParameter(Description = "Whose name to unlock (you if left out)")] GuildUser? user = null)
    {
        if (await ModuleOffAsync() is { } off)
            return off;

        var targetId = user?.Id ?? ActorId;
        var rules = await RulesAsync();
        var now = time.GetUtcNow();
        await using var db = await dbFactory.CreateDbContextAsync();

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

    [SlashCommand("paint", "Give someone a name colour for a while", Contexts = [InteractionContextType.Guild])]
    public async Task<InteractionMessageProperties> PaintAsync(
        [SlashCommandParameter(Description = "Who to paint")] GuildUser user,
        [SlashCommandParameter(Description = "A colour name or a hex code like #ff00ff", AutocompleteProviderType = typeof(PaintColourAutocomplete))] string colour,
        [SlashCommandParameter(Description = "How many hours", MinValue = 1, MaxValue = 168)] int hours)
    {
        if (await ModuleOffAsync() is { } off)
            return off;
        if (!PaintColours.TryParse(colour, out var rgb, out var colourName))
            return Replies.Ephemeral("That isn't a colour I know. Pick one from the list or use a hex code like `#ff00ff`.");

        var rules = await RulesAsync();
        if (hours > rules.PaintMaxHours)
            return Replies.Ephemeral($"Paint lasts at most {rules.PaintMaxHours} hours.");

        var now = time.GetUtcNow();
        await using var db = await dbFactory.CreateDbContextAsync();
        var self = user.Id == ActorId;
        if (!self && await MischiefEffects.ActiveAsync(db, Guild.Id, user.Id, MischiefEffectKinds.Shield, now) is { } shield)
            return Replies.Ephemeral($"<@{user.Id}> is shielded until <t:{shield.EndsAt.ToUnixTimeSeconds()}:t>.");

        var price = await PriceAsync(self ? rules.SelfPaintPrice(hours) : rules.PaintPrice(hours));
        if (await PayAsync(price, $"paint {user.Id} {colourName} {hours}h", self ? $"Painting yourself for {hours} h" : $"Painting <@{user.Id}> for {hours} h") is { } unpaid)
            return unpaid;

        if (await paints.ApplyAsync(Guild.Id, user.Id, rgb, colourName) is not { } roleId)
        {
            await RefundAsync(price, $"paint {user.Id} refused by Discord");
            return Replies.Ephemeral("Discord won't let me do that: I need Manage Roles, and my role has to be above the member's top role.");
        }

        // A new coat replaces the old one.
        if (await MischiefEffects.ActiveAsync(db, Guild.Id, user.Id, MischiefEffectKinds.Paint, now) is { } old)
        {
            await paints.RemoveAsync(old);
            old.EndsAt = now;
            old.EndedById = ActorId;
        }

        var paint = AddEffect(db, MischiefEffectKinds.Paint, user.Id, price, now, now + TimeSpan.FromHours(hours));
        paint.RoleId = roleId;
        await db.SaveChangesAsync();

        return Public($"<@{ActorId}> painted {(self ? "themselves" : $"<@{user.Id}>")} **{colourName}** until <t:{paint.EndsAt.ToUnixTimeSeconds()}:t>{Paid(price)}.");
    }

    [SlashCommand("unpaint", "Remove a name colour early (costs more than the paint did)", Contexts = [InteractionContextType.Guild])]
    public async Task<InteractionMessageProperties> UnpaintAsync(
        [SlashCommandParameter(Description = "Who to clean up (you if left out)")] GuildUser? user = null)
    {
        if (await ModuleOffAsync() is { } off)
            return off;

        var targetId = user?.Id ?? ActorId;
        var rules = await RulesAsync();
        var now = time.GetUtcNow();
        await using var db = await dbFactory.CreateDbContextAsync();

        if (await MischiefEffects.ActiveAsync(db, Guild.Id, targetId, MischiefEffectKinds.Paint, now) is not { } paint)
            return Replies.Ephemeral($"<@{targetId}> isn't painted.");

        var price = await PriceAsync(rules.PaintBreakPrice(paint, now));
        if (await PayAsync(price, $"unpaint {targetId}", $"Removing <@{targetId}>'s paint") is { } unpaid)
            return unpaid;

        await paints.RemoveAsync(paint);
        paint.EndsAt = now;
        paint.EndedById = ActorId;
        await db.SaveChangesAsync();

        return Public($"<@{ActorId}> washed the paint off <@{targetId}>{Paid(price)}.");
    }

    private async Task<InteractionMessageProperties?> ModuleOffAsync()
        => await modules.IsEnabledAsync(Guild.Id, ModuleId) ? null : Replies.Ephemeral($"The `{ModuleId}` module is off.");

    private ValueTask<MischiefRules> RulesAsync() => settings.GetAsync<MischiefRules>(Guild.Id, ModuleId);

    // Free while the points module is off.
    private async Task<double> PriceAsync(double price) => await points.ChargesAsync(Guild.Id) ? price : 0;

    private async Task<InteractionMessageProperties?> PayAsync(double price, string ledgerReason, string what)
    {
        if (price <= 0 || await points.TrySpendAsync(Guild.Id, ActorId, price, ledgerReason))
            return null;

        var balance = (await points.GetAsync(Guild.Id, ActorId))?.Balance ?? 0;
        return Replies.Ephemeral($"{what} costs {Format(price)}; you have {Format(balance)}.");
    }

    private async Task RefundAsync(double price, string reason)
    {
        if (price > 0)
            await points.RefundAsync(Guild.Id, ActorId, price, reason);
    }

    private string Format(double amount) => points.Rules(Guild.Id).Format(amount);

    private string Paid(double price) => price > 0 ? $" ({Format(price)})" : "";

    private static InteractionMessageProperties? CooldownReply(MischiefRules rules, RenameHistory history, ulong userId, DateTimeOffset now)
    {
        if (history.LastAt is not { } last || now - last >= TimeSpan.FromMinutes(rules.RenameCooldownMinutes))
            return null;

        var until = last + TimeSpan.FromMinutes(rules.RenameCooldownMinutes);
        return Replies.Ephemeral($"<@{userId}> was renamed <t:{last.ToUnixTimeSeconds()}:R>. They can be renamed again <t:{until.ToUnixTimeSeconds()}:R>.");
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

    private async Task RecordRenameAsync(BotDbContext db, GuildUser target, string? name, string? reason, double price, DateTimeOffset now)
    {
        db.Renames.Add(new()
        {
            GuildId = Guild.Id,
            ActorId = ActorId,
            TargetId = target.Id,
            OldName = target.Nickname,
            NewName = name,
            Reason = reason,
            Cost = price,
            CreatedAt = now,
        });
        await db.SaveChangesAsync();
    }

    private MischiefEffect AddEffect(BotDbContext db, string kind, ulong targetId, double paid, DateTimeOffset now, DateTimeOffset endsAt)
    {
        var effect = new MischiefEffect
        {
            GuildId = Guild.Id,
            Kind = kind,
            TargetId = targetId,
            ActorId = ActorId,
            Paid = paid,
            CreatedAt = now,
            EndsAt = endsAt,
        };
        db.MischiefEffects.Add(effect);
        return effect;
    }

    private static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static InteractionMessageProperties Public(string content) => new()
    {
        Content = content,
        AllowedMentions = AllowedMentionsProperties.None,
    };
}

public sealed class PaintColourAutocomplete : IAutocompleteProvider<AutocompleteInteractionContext>
{
    public ValueTask<IEnumerable<ApplicationCommandOptionChoiceProperties>?> GetChoicesAsync(
        ApplicationCommandInteractionDataOption option,
        AutocompleteInteractionContext context)
    {
        var input = option.Value ?? "";
        var choices = PaintColours.Named.Keys
            .Where(name => name.Contains(input, StringComparison.OrdinalIgnoreCase))
            .Select(name => new ApplicationCommandOptionChoiceProperties(name, name));
        return new(choices);
    }
}
