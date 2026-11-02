using Microsoft.EntityFrameworkCore;

using NetCord;
using NetCord.Gateway;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;
using NetCord.Services.ComponentInteractions;

using NodaTime;

using THOBOTTO.Data;
using THOBOTTO.Discord;
using THOBOTTO.Members;
using THOBOTTO.Notifications;
using THOBOTTO.Panel;

namespace THOBOTTO.Events;

// Inviting someone to one of your upcoming sessions or events (planned by you or with you in): a DM with I'm in.
public sealed class EventInvites(IDbContextFactory<BotDbContext> dbFactory, EventBoard board, RestClient rest, TimeZones zones, TimeProvider time, ILogger<EventInvites> logger)
{
    public async Task<IReadOnlyList<Event>> YoursAsync(ulong guildId, ulong userId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var now = time.GetUtcNow();
        var mine = db.EventRsvps.Where(r => r.UserId == userId && r.Status == RsvpStatuses.In).Select(r => r.EventId);
        return await db.Events.AsNoTracking()
            .Where(e => e.GuildId == guildId && e.State == EventStates.Scheduled && e.StartsAt > now && (e.CreatorId == userId || mine.Contains(e.Id)))
            .OrderBy(e => e.StartsAt).Take(25).ToListAsync();
    }

    // Each with its start in the inviter's time.
    public async Task<IReadOnlyList<(Event Event, string Starts)>> ListedAsync(ulong guildId, ulong userId)
    {
        var (zone, _) = await zones.ForAsync(guildId, userId);
        return [.. (await YoursAsync(guildId, userId)).Select(e => (e, Instant.FromDateTimeOffset(e.StartsAt!.Value).InZone(zone).ToString("ddd d MMM HH:mm", null)))];
    }

    public static string? Refused(User inviter, User user)
        => user.IsBot ? "Bots don't come to sessions." : user.Id == inviter.Id ? "That's you: invite someone else." : null;

    public const string NothingComing = "You have nothing coming up to invite them to: plan something, or sign up for something.";

    public async Task<string> SendAsync(Guild guild, User from, ulong userId, long eventId)
    {
        if ((await YoursAsync(guild.Id, from.Id)).FirstOrDefault(e => e.Id == eventId) is not { StartsAt: { } start } e)
            return "That isn't one of your upcoming sessions or events.";
        var at = start.ToUnixTimeSeconds();
        var link = e.MessageId is { } message ? $" · [see it]({Notifier.Link(e.GuildId, e.ChannelId, message)})" : "";
        try
        {
            var dm = await rest.GetDMChannelAsync(userId);
            await dm.SendMessageAsync(new()
            {
                Content = $"**{from.GlobalName ?? from.Username}** invites you to **{e.Title}** in **{guild.Name}**: <t:{at}:F> (<t:{at}:R>){link}",
                Components = [new ActionRowProperties { new ButtonProperties($"eventinvitein:{guild.Id}:{e.Id}", "I'm in", ButtonStyle.Success) }],
            });
            return $"Invited <@{userId}> to **{e.Title}**.";
        }
        catch (RestException ex)
        {
            logger.LogDebug("Inviting {UserId} to event {EventId} failed: {Message}", userId, e.Id, ex.Message);
            return $"<@{userId}> doesn't take DMs from the bot: tell them yourself, it's <t:{at}:F>.";
        }
    }

    // I'm in on the invitation: signs them up.
    public Task<string> InAsync(long eventId, ulong userId) => board.RsvpAsync(eventId, userId, RsvpStatuses.In);
}

// Right-click a member, Apps, Invite to a session; /event invite and /session invite.
public sealed class EventInviteCommand(EventInvites invites) : GuildCommandModule
{
    [UserCommand("Invite to a session", Contexts = [InteractionContextType.Guild])]
    public async Task<InteractionCallbackProperties> InviteAsync(User user)
    {
        if (EventInvites.Refused(Context.User, user) is { } refused)
            return InteractionCallback.Message(Replies.Ephemeral(refused));
        var listed = await invites.ListedAsync(GuildId, Context.User.Id);
        if (listed.Count == 0)
            return InteractionCallback.Message(Replies.Ephemeral(EventInvites.NothingComing));
        var options = listed.Select(l => new StringMenuSelectOptionProperties(l.Event.Title.Length <= 100 ? l.Event.Title : l.Event.Title[..100], l.Event.Id.ToString()) { Description = l.Starts });
        return InteractionCallback.Modal(new ModalProperties($"eventinvite:{user.Id}", "Invite to a session")
        {
            new LabelProperties("Which one?", new StringMenuProperties("event", options)),
        });
    }
}

public sealed class EventInviteModal(EventInvites invites) : GuildModalModule
{
    [ComponentInteraction("eventinvite")]
    public async Task InviteAsync(ulong userId)
    {
        var picked = Context.Components.OfType<Label>().Select(l => l.Component).OfType<StringMenu>().FirstOrDefault()?.SelectedValues is [var value, ..] && long.TryParse(value, out var id) ? id : 0;
        await RespondAsync(InteractionCallback.DeferredMessage(MessageFlags.Ephemeral));
        var result = await invites.SendAsync(Guild, Context.User, userId, picked);
        await ModifyResponseAsync(m => (m.Content, m.AllowedMentions) = (result, AllowedMentionsProperties.None));
    }
}

public static class EventInviting
{
    public static async Task InviteAsync(GuildCommandModule command, EventInvites invites, User member, long eventId)
    {
        if (EventInvites.Refused(command.Context.User, member) is { } refused)
        {
            await command.RespondAsync(InteractionCallback.Message(Replies.Ephemeral(refused)));
            return;
        }
        await command.RespondAsync(InteractionCallback.DeferredMessage(MessageFlags.Ephemeral));
        var result = await invites.SendAsync(command.Context.Guild!, command.Context.User, member.Id, eventId);
        await command.ModifyResponseAsync(m => (m.Content, m.AllowedMentions) = (result, AllowedMentionsProperties.None));
    }
}

// The events and sessions you can invite to.
public sealed class InvitableAutocomplete(EventInvites invites) : IAutocompleteProvider<AutocompleteInteractionContext>
{
    public async ValueTask<IEnumerable<ApplicationCommandOptionChoiceProperties>?> GetChoicesAsync(ApplicationCommandInteractionDataOption option, AutocompleteInteractionContext context)
    {
        var input = option.Value ?? "";
        var listed = await invites.ListedAsync(context.Interaction.GuildId!.Value, context.User.Id);
        return listed
            .Where(l => l.Event.Title.Contains(input, StringComparison.OrdinalIgnoreCase))
            .Select(l => (l.Event.Id, Label: $"{l.Event.Title} · {l.Starts}"))
            .Select(c => new ApplicationCommandOptionChoiceProperties(c.Label.Length <= 100 ? c.Label : c.Label[..100], c.Id));
    }
}

// I'm in, pressed in the invitation's DM: signs them up, if they're still in the server.
public sealed class EventInviteButtons(EventInvites invites, PanelAccess members) : ComponentInteractionModule<ButtonInteractionContext>
{
    [ComponentInteraction("eventinvitein")]
    public async Task InAsync(ulong guildId, long eventId)
    {
        await RespondAsync(InteractionCallback.DeferredModifyMessage);
        var said = await members.InAsync(guildId, Context.User.Id) is null ? "You're not in that server any more." : await invites.InAsync(eventId, Context.User.Id);
        await ModifyResponseAsync(m => (m.Content, m.Components) = ($"{Context.Message.Content}\n{said}", []));
    }
}
