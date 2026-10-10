using Microsoft.EntityFrameworkCore;

using NetCord;
using NetCord.Gateway;
using NetCord.Rest;
using NetCord.Services.ComponentInteractions;

using THOBOTTO.Access;
using THOBOTTO.Data;
using THOBOTTO.Modules;

namespace THOBOTTO.Moderation;

// Appeals against bans: the Appeal button on a ban's DM opens a form; the appeal goes to the server's appeals
// channel, where a moderator who may lift bans unbans or rejects it, and the member is told. One per ban.
public sealed class BanAppeals(
    IDbContextFactory<BotDbContext> dbFactory,
    CaseBook cases,
    ModActions actions,
    SettingsStore settings,
    AccessControl access,
    RestClient rest,
    GatewayClient gateway,
    TimeProvider time)
{
    public const int MaxText = 1500;

    // The form, or why there's none (closed, too early, already appealed: what became of it).
    public async Task<InteractionCallbackProperties> OpenAsync(ulong guildId, ulong userId)
    {
        var (ban, problem) = await BanAsync(guildId, userId);
        if (problem is not null)
            return Private(problem);
        return InteractionCallback.Modal(new ModalProperties($"appealtext:{guildId}", "Appeal your ban")
        {
            new LabelProperties("Why should you be let back in?", new TextInputProperties("text", TextInputStyle.Paragraph) { MaxLength = MaxText, MinLength = 20 }),
        });
    }

    public async Task<string> SubmitAsync(ulong guildId, User user, string text)
    {
        var (ban, problem) = await BanAsync(guildId, user.Id);
        if (problem is not null)
            return problem;
        var rules = await settings.GetAsync<ModRules>(guildId, CaseBook.ModuleId);
        await using var db = await dbFactory.CreateDbContextAsync();
        var appeal = new ModAppeal { GuildId = guildId, UserId = user.Id, CaseNumber = ban!.Number, Text = text.Trim(), CreatedAt = time.GetUtcNow() };
        db.ModAppeals.Add(appeal);
        await db.SaveChangesAsync();
        try
        {
            var posted = await rest.SendMessageAsync(rules.AppealsChannelId!.Value, new()
            {
                Embeds = [Embed(appeal, ban, user)],
                Components = Buttons(appeal),
                AllowedMentions = AllowedMentionsProperties.None,
            });
            (appeal.ChannelId, appeal.MessageId) = (posted.ChannelId, posted.Id);
            await db.SaveChangesAsync();
        }
        catch (RestException)
        {
            db.ModAppeals.Remove(appeal);
            await db.SaveChangesAsync();
            return "The server can't take appeals right now; try again later.";
        }
        return "Your appeal was sent. You'll hear back here (or press Appeal again to see where it stands).";
    }

    // A moderator's decision; what to tell them.
    public async Task<string> DecideAsync(long appealId, GuildUser moderator, bool unban)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        if (await db.ModAppeals.FindAsync(appealId) is not { } appeal || !gateway.Cache.Guilds.TryGetValue(appeal.GuildId, out var guild))
            return "That appeal is gone.";
        var permission = CaseTypes.LiftPermission(CaseTypes.Ban);
        if (!await access.CanAsync(guild, moderator, permission))
            return $"Deciding appeals needs `{permission}`.";
        if (appeal.Decision is not null)
            return $"<@{appeal.DecidedById}> already decided: {appeal.Decision}.";

        var result = unban
            ? await actions.UnbanAsync(new(guild.Id, moderator.Id, moderator.Username), appeal.UserId, $"appeal accepted (case #{appeal.CaseNumber})")
            : $"Rejected <@{appeal.UserId}>'s appeal.";
        (appeal.Decision, appeal.DecidedById, appeal.DecidedAt) = (unban ? AppealDecisions.Unbanned : AppealDecisions.Rejected, moderator.Id, time.GetUtcNow());
        await db.SaveChangesAsync();
        await cases.DmAsync(guild.Id, appeal.UserId, unban
            ? "your ban appeal was accepted: you're unbanned and can join again."
            : "your ban appeal was rejected.");
        if (appeal is { ChannelId: { } channelId, MessageId: { } messageId })
        {
            try
            {
                await rest.ModifyMessageAsync(channelId, messageId, m => m.Components = [new ActionRowProperties
                {
                    new ButtonProperties("appealdone", unban ? $"Unbanned by {moderator.Username}" : $"Rejected by {moderator.Username}", ButtonStyle.Secondary) { Disabled = true },
                }]);
            }
            catch (RestException)
            {
            }
        }
        return result;
    }

    // The ban an appeal would be about, or why it can't be appealed.
    private async Task<(ModCase? Ban, string? Problem)> BanAsync(ulong guildId, ulong userId)
    {
        var rules = await settings.GetAsync<ModRules>(guildId, CaseBook.ModuleId);
        if (rules.AppealsChannelId is null)
            return (null, "This server doesn't take ban appeals.");
        var ban = (await cases.HistoryAsync(guildId, userId)).Where(c => c.Type == CaseTypes.Ban).MaxBy(c => c.Number);
        if (ban is null || ban.EndedAt is not null)
            return (null, "You aren't banned there any more.");
        await using var db = await dbFactory.CreateDbContextAsync();
        if (await db.ModAppeals.AsNoTracking().FirstOrDefaultAsync(a => a.GuildId == guildId && a.UserId == userId && a.CaseNumber == ban.Number) is { } earlier)
            return (null, earlier.Decision switch
            {
                AppealDecisions.Rejected => "Your appeal was rejected.",
                AppealDecisions.Unbanned => "Your appeal was accepted.",
                _ => $"Your appeal from <t:{earlier.CreatedAt.ToUnixTimeSeconds()}:R> is waiting for the moderators.",
            });
        var opens = ban.CreatedAt + TimeSpan.FromDays(rules.AppealAfterDays);
        if (opens > time.GetUtcNow())
            return (null, $"You can appeal from <t:{opens.ToUnixTimeSeconds()}:f>.");
        return (ban, null);
    }

    private static EmbedProperties Embed(ModAppeal appeal, ModCase ban, User user) => new()
    {
        Title = $"Ban appeal from {user.Username}",
        Description = $"<@{user.Id}> ({user.Id}), banned in case #{ban.Number} <t:{ban.CreatedAt.ToUnixTimeSeconds()}:R>: {ban.Reason ?? "no reason given"}\n\n{appeal.Text}",
        Color = new(0xF0B232),
    };

    private static IEnumerable<IMessageComponentProperties> Buttons(ModAppeal appeal) =>
    [
        new ActionRowProperties
        {
            new ButtonProperties($"appealdecide:{appeal.Id}:unban", "Unban", EmojiProperties.Standard("🔓"), ButtonStyle.Success),
            new ButtonProperties($"appealdecide:{appeal.Id}:reject", "Reject", EmojiProperties.Standard("✖️"), ButtonStyle.Danger),
        },
    ];

    private static InteractionCallbackProperties Private(string text) => InteractionCallback.Message(new() { Content = text, Flags = MessageFlags.Ephemeral });
}

// The Appeal button in a ban's DM.
public sealed class AppealButton(BanAppeals appeals) : ComponentInteractionModule<ButtonInteractionContext>
{
    [ComponentInteraction("appeal")]
    public Task<InteractionCallbackProperties> OpenAsync(ulong guildId) => appeals.OpenAsync(guildId, Context.User.Id);

    [ComponentInteraction("appealdecide")]
    public async Task DecideAsync(long appealId, string decision)
    {
        if (Context.User is not GuildUser moderator)
            return;
        await RespondAsync(InteractionCallback.DeferredMessage(MessageFlags.Ephemeral));
        var result = await appeals.DecideAsync(appealId, moderator, decision == "unban");
        await ModifyResponseAsync(m => (m.Content, m.AllowedMentions) = (result, AllowedMentionsProperties.None));
    }
}

public sealed class AppealForm(BanAppeals appeals) : ComponentInteractionModule<ModalInteractionContext>
{
    [ComponentInteraction("appealtext")]
    public async Task<InteractionCallbackProperties> SubmitAsync(ulong guildId)
    {
        var text = Context.Components.OfType<Label>().Select(l => l.Component).OfType<TextInput>().First().Value;
        return InteractionCallback.Message(new() { Content = await appeals.SubmitAsync(guildId, Context.User, text), Flags = MessageFlags.Ephemeral });
    }
}
