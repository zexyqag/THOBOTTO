using System.Net;

using NetCord;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;
using NetCord.Services.ComponentInteractions;

using THOBOTTO.Access;
using THOBOTTO.Discord;
using THOBOTTO.Modules;
using THOBOTTO.Notifications;

namespace THOBOTTO.Moderation;

// Any member can report a message, or a member with /report: it goes to the moderation log as a card with who and
// why, for the moderators to act on (delete and warn, or warn), or mark handled.
public sealed class ReportCommand(SettingsStore settings, ModuleState modules, RestClient rest, ILogger<ReportCommand> logger) : GuildCommandModule
{
    [MessageCommand("Report to moderators", Contexts = [InteractionContextType.Guild])]
    public async Task<InteractionCallbackProperties> ReportAsync(RestMessage message)
    {
        var refusal = await ReportCards.NoLogAsync(settings, modules, GuildId)
            ?? (message.Author.Id == Context.User.Id ? "That's your own message."
            : message.Author.IsBot ? "That's a bot's message: tell the moderators directly."
            : null);
        return refusal is not null
            ? InteractionCallback.Message(Replies.Ephemeral(refusal))
            : InteractionCallback.Modal(new ModalProperties($"modreport:{message.ChannelId}:{message.Id}", "Report to moderators")
            {
                new LabelProperties("Why? (optional, only the moderators see it)", new TextInputProperties("why", TextInputStyle.Paragraph) { Required = false, MaxLength = 500 }),
            });
    }

    [SlashCommand("report", "Tell the moderators about a member; only they see it (a message: right-click it, Apps)", Contexts = [InteractionContextType.Guild])]
    public async Task ReportMemberAsync(
        [SlashCommandParameter(Description = "Who")] User member,
        [SlashCommandParameter(Description = "What happened; only the moderators see it", MaxLength = 500)] string why)
    {
        var refusal = await ReportCards.NoLogAsync(settings, modules, GuildId)
            ?? (member.Id == Context.User.Id ? "That's you."
            : member.IsBot ? "That's a bot: tell the moderators directly."
            : null);
        if (refusal is not null)
        {
            await RespondAsync(InteractionCallback.Message(Replies.Ephemeral(refusal)));
            return;
        }
        await RespondAsync(InteractionCallback.DeferredMessage(MessageFlags.Ephemeral));
        string result;
        try
        {
            var log = (await settings.GetAsync<ModRules>(GuildId, CaseBook.ModuleId)).LogChannelId!.Value;
            await rest.SendMessageAsync(log, ReportCards.Of("🚩 A member reported", $"<@{member.Id}>\nReported by <@{Context.User.Id}>: {why.Trim()}",
                new ButtonProperties($"modreportwarnmember:{member.Id}", "Warn", ButtonStyle.Danger)));
            result = "Sent to the moderators. Thanks for telling them.";
        }
        catch (RestException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden)
        {
            logger.LogWarning("Reporting member {UserId} in {GuildId} failed: {Message}", member.Id, GuildId, ex.Message);
            result = ReportCards.Unpostable;
        }
        await ModifyResponseAsync(m => m.Content = result);
    }
}

public static class ReportCards
{
    public const string Unpostable = "The moderation log can't be posted in: tell a moderator.";

    public static async Task<string?> NoLogAsync(SettingsStore settings, ModuleState modules, ulong guildId)
        => !await modules.IsEnabledAsync(guildId, CaseBook.ModuleId) || (await settings.GetAsync<ModRules>(guildId, CaseBook.ModuleId)).LogChannelId is null
            ? "This server has no moderation log to report to."
            : null;

    // A report in the moderation log: what the moderators can do about it, and Handled.
    public static MessageProperties Of(string title, string description, ButtonProperties act) => new()
    {
        Embeds = [new EmbedProperties { Title = title, Description = description }],
        Components = [new ActionRowProperties { act, new ButtonProperties("modreportdone", "Handled", ButtonStyle.Secondary) }],
        AllowedMentions = AllowedMentionsProperties.None,
    };
}

public sealed class ReportModal(SettingsStore settings, RestClient rest, ILogger<ReportModal> logger) : GuildModalModule
{
    private const int Shown = 1000;

    [ComponentInteraction("modreport")]
    public async Task ReportAsync(ulong channelId, ulong messageId)
    {
        var why = Context.Components.OfType<Label>().Select(l => l.Component).OfType<TextInput>().FirstOrDefault()?.Value.Trim();
        await RespondAsync(InteractionCallback.DeferredMessage(MessageFlags.Ephemeral));
        string result;
        try
        {
            var message = await rest.GetMessageAsync(channelId, messageId);
            var log = (await settings.GetAsync<ModRules>(GuildId, CaseBook.ModuleId)).LogChannelId ?? throw new InvalidOperationException("no log");
            var text = message.Content.Length > Shown ? message.Content[..Shown] + "…" : message.Content;
            await rest.SendMessageAsync(log, ReportCards.Of("🚩 A message reported",
                (string.IsNullOrWhiteSpace(text) ? "*(no text)*" : string.Join('\n', text.Split('\n').Select(l => $"> {l}")))
                    + $"\n\nBy <@{message.Author.Id}> in <#{channelId}> · [the message]({Notifier.Link(GuildId, channelId, messageId)})"
                    + $"\nReported by <@{Context.User.Id}>{(string.IsNullOrEmpty(why) ? "" : $": {why}")}",
                new ButtonProperties($"modreportwarn:{channelId}:{messageId}", "Delete and warn", ButtonStyle.Danger)));
            result = "Sent to the moderators. Thanks for telling them.";
        }
        catch (RestException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden)
        {
            logger.LogWarning("Reporting message {MessageId} in {GuildId} failed: {Message}", messageId, GuildId, ex.Message);
            result = ex.StatusCode == HttpStatusCode.NotFound ? "That message is gone." : ReportCards.Unpostable;
        }
        await ModifyResponseAsync(m => m.Content = result);
    }
}

// The moderators' buttons on a report: Delete and warn, or Warn, asks why (as the menu entries do), Handled closes it.
public sealed class ReportButtons(AccessControl access) : GuildButtonModule
{
    [ComponentInteraction("modreportwarn")]
    public async Task<InteractionCallbackProperties> DeleteAndWarnAsync(ulong channelId, ulong messageId)
    {
        var actor = (GuildUser)Context.User;
        if (!await access.CanAsync(Guild, actor, BotPermissions.ModMessages) || !await access.CanAsync(Guild, actor, BotPermissions.ModWarn))
            return InteractionCallback.Message(Replies.Ephemeral($"That needs `{BotPermissions.ModMessages}` and `{BotPermissions.ModWarn}`."));
        return InteractionCallback.Modal(new ModalProperties($"moddelwarn:{channelId}:{messageId}", "Delete and warn") { ModForms.Why(required: true) });
    }

    [ComponentInteraction("modreportwarnmember")]
    public async Task<InteractionCallbackProperties> WarnAsync(ulong userId)
        => await access.CanAsync(Guild, (GuildUser)Context.User, BotPermissions.ModWarn)
            ? InteractionCallback.Modal(ModForms.Warn(userId))
            : InteractionCallback.Message(Replies.Ephemeral($"That needs `{BotPermissions.ModWarn}`."));

    [ComponentInteraction("modreportdone")]
    public async Task<InteractionCallbackProperties> HandledAsync()
    {
        if (!await access.CanAsync(Guild, (GuildUser)Context.User, BotPermissions.ModWarn))
            return InteractionCallback.Message(Replies.Ephemeral($"That needs `{BotPermissions.ModWarn}`."));
        var embed = Context.Message.Embeds.FirstOrDefault();
        return InteractionCallback.ModifyMessage(m =>
        {
            m.Embeds = embed is null ? [] : [new EmbedProperties { Title = $"✅ {embed.Title?.Replace("🚩 ", "")}, handled", Description = $"{embed.Description}\nHandled by <@{Context.User.Id}>" }];
            m.Components = [];
        });
    }
}
