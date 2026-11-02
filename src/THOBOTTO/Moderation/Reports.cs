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

// Any member can report a message: it goes to the moderation log as a card with the message, who wrote and reported
// it, and why, for the moderators to delete and warn, or mark handled.
public sealed class ReportCommand(SettingsStore settings, ModuleState modules) : GuildCommandModule
{
    [MessageCommand("Report to moderators", Contexts = [InteractionContextType.Guild])]
    public async Task<InteractionCallbackProperties> ReportAsync(RestMessage message)
    {
        var refusal = !await modules.IsEnabledAsync(GuildId, CaseBook.ModuleId) || (await settings.GetAsync<ModRules>(GuildId, CaseBook.ModuleId)).LogChannelId is null
                ? "This server has no moderation log to report to."
            : message.Author.Id == Context.User.Id ? "That's your own message."
            : message.Author.IsBot ? "That's a bot's message: tell the moderators directly."
            : null;
        return refusal is not null
            ? InteractionCallback.Message(Replies.Ephemeral(refusal))
            : InteractionCallback.Modal(new ModalProperties($"modreport:{message.ChannelId}:{message.Id}", "Report to moderators")
            {
                new LabelProperties("Why? (optional, only the moderators see it)", new TextInputProperties("why", TextInputStyle.Paragraph) { Required = false, MaxLength = 500 }),
            });
    }
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
            await rest.SendMessageAsync(log, new()
            {
                Embeds =
                [
                    new EmbedProperties
                    {
                        Title = "🚩 A message reported",
                        Description = (string.IsNullOrWhiteSpace(text) ? "*(no text)*" : string.Join('\n', text.Split('\n').Select(l => $"> {l}")))
                            + $"\n\nBy <@{message.Author.Id}> in <#{channelId}> · [the message]({Notifier.Link(GuildId, channelId, messageId)})"
                            + $"\nReported by <@{Context.User.Id}>{(string.IsNullOrEmpty(why) ? "" : $": {why}")}",
                    },
                ],
                Components =
                [
                    new ActionRowProperties
                    {
                        new ButtonProperties($"modreportwarn:{channelId}:{messageId}", "Delete and warn", ButtonStyle.Danger),
                        new ButtonProperties("modreportdone", "Handled", ButtonStyle.Secondary),
                    },
                ],
                AllowedMentions = AllowedMentionsProperties.None,
            });
            result = "Sent to the moderators. Thanks for telling them.";
        }
        catch (RestException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden)
        {
            logger.LogWarning("Reporting message {MessageId} in {GuildId} failed: {Message}", messageId, GuildId, ex.Message);
            result = ex.StatusCode == HttpStatusCode.NotFound ? "That message is gone." : "The moderation log can't be posted in: tell a moderator.";
        }
        await ModifyResponseAsync(m => m.Content = result);
    }
}

// The moderators' buttons on a report: Delete and warn asks why (as the menu entry does), Handled closes it.
public sealed class ReportButtons(AccessControl access) : GuildButtonModule
{
    [ComponentInteraction("modreportwarn")]
    public async Task<InteractionCallbackProperties> DeleteAndWarnAsync(ulong channelId, ulong messageId)
    {
        var actor = (GuildUser)Context.User;
        if (!await access.CanAsync(Guild, actor, BotPermissions.ModMessages) || !await access.CanAsync(Guild, actor, BotPermissions.ModWarn))
            return InteractionCallback.Message(Replies.Ephemeral($"That needs `{BotPermissions.ModMessages}` and `{BotPermissions.ModWarn}`."));
        return InteractionCallback.Modal(new ModalProperties($"moddelwarn:{channelId}:{messageId}", "Delete and warn")
        {
            new LabelProperties("Why? The member sees this.", new TextInputProperties("reason", TextInputStyle.Short) { MaxLength = 500 }),
        });
    }

    [ComponentInteraction("modreportdone")]
    public async Task<InteractionCallbackProperties> HandledAsync()
    {
        if (!await access.CanAsync(Guild, (GuildUser)Context.User, BotPermissions.ModWarn))
            return InteractionCallback.Message(Replies.Ephemeral($"That needs `{BotPermissions.ModWarn}`."));
        var embed = Context.Message.Embeds.FirstOrDefault();
        return InteractionCallback.ModifyMessage(m =>
        {
            m.Embeds = embed is null ? [] : [new EmbedProperties { Title = "✅ A message reported, handled", Description = $"{embed.Description}\nHandled by <@{Context.User.Id}>" }];
            m.Components = [];
        });
    }
}
