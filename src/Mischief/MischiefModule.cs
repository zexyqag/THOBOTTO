using System.Net;

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

// What /rename and /name share: payment, cooldowns, effects, and replying in public.
public abstract class MischiefModule(
    ModuleState modules,
    SettingsStore settings,
    PointsEngine points,
    PaintRoles paints,
    Notifier notifier,
    IDbContextFactory<BotDbContext> dbFactory,
    TimeProvider time) : ApplicationCommandModule<ApplicationCommandContext>
{
    public const string ModuleId = "mischief";

    protected ModuleState Modules { get; } = modules;

    protected SettingsStore Settings { get; } = settings;

    protected PointsEngine Points { get; } = points;

    protected PaintRoles Paints { get; } = paints;

    protected Notifier Notifier { get; } = notifier;

    protected IDbContextFactory<BotDbContext> DbFactory { get; } = dbFactory;

    protected TimeProvider Time { get; } = time;

    protected Guild Guild => Context.Guild!;

    protected ulong ActorId => Context.User.Id;

    // Mischief calls Discord (nicknames, roles) and sends DMs, which can take longer than the 3 seconds
    // Discord waits. So: defer privately; errors fill that in. Public announcements go to the channel
    // as ordinary messages (a follow-up would take over the private placeholder and stay private),
    // and the placeholder is removed.
    protected Task DeferAsync() => RespondAsync(InteractionCallback.DeferredMessage(MessageFlags.Ephemeral));

    protected async Task FinishAsync(InteractionMessageProperties result)
    {
        if (result.Flags?.HasFlag(MessageFlags.Ephemeral) == true)
        {
            await ModifyResponseAsync(m => m.Content = result.Content);
            return;
        }

        await Context.Channel.SendMessageAsync(new() { Content = result.Content, AllowedMentions = result.AllowedMentions });
        await DeleteResponseAsync();
    }

    protected async Task<InteractionMessageProperties?> ModuleOffAsync()
        => await Modules.IsEnabledAsync(Guild.Id, ModuleId) ? null : Replies.Ephemeral($"The `{ModuleId}` module is off.");

    protected ValueTask<MischiefRules> RulesAsync() => Settings.GetAsync<MischiefRules>(Guild.Id, ModuleId);

    // Free while the points module is off.
    protected async Task<double> PriceAsync(double price) => await Points.ChargesAsync(Guild.Id) ? price : 0;

    protected async Task<InteractionMessageProperties?> PayAsync(double price, string ledgerReason, string what)
    {
        if (price <= 0 || await Points.TrySpendAsync(Guild.Id, ActorId, price, ledgerReason))
            return null;

        var balance = (await Points.GetAsync(Guild.Id, ActorId))?.Balance ?? 0;
        return Replies.Ephemeral($"{what} costs {Format(price)}; you have {Format(balance)}.");
    }

    protected async Task RefundAsync(double price, string reason)
    {
        if (price > 0)
            await Points.RefundAsync(Guild.Id, ActorId, price, reason);
    }

    protected string Format(double amount) => Points.Rules(Guild.Id).Format(amount);

    protected string Paid(double price) => price > 0 ? $" ({Format(price)})" : "";

    protected static InteractionMessageProperties? CooldownReply(MischiefRules rules, RenameHistory history, ulong userId, DateTimeOffset now)
    {
        if (history.LastAt is not { } last || now - last >= TimeSpan.FromMinutes(rules.RenameCooldownMinutes))
            return null;

        var until = last + TimeSpan.FromMinutes(rules.RenameCooldownMinutes);
        return Replies.Ephemeral($"<@{userId}> was renamed <t:{last.ToUnixTimeSeconds()}:R>. They can be renamed again <t:{until.ToUnixTimeSeconds()}:R>.");
    }

    protected static async Task<bool> SetNicknameAsync(GuildUser user, string? name)
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

    protected async Task RecordRenameAsync(BotDbContext db, GuildUser target, string? name, string? reason, double price, DateTimeOffset now)
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

    protected MischiefEffect AddEffect(BotDbContext db, string kind, ulong targetId, double paid, DateTimeOffset now, DateTimeOffset endsAt)
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

    protected static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    protected static InteractionMessageProperties Public(string content) => new()
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
