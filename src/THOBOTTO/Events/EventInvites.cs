using Microsoft.EntityFrameworkCore;

using NetCord;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;
using NetCord.Services.ComponentInteractions;

using THOBOTTO.Data;
using THOBOTTO.Discord;
using THOBOTTO.Members;
using THOBOTTO.Notifications;
using THOBOTTO.Panel;

namespace THOBOTTO.Events;

// Right-click a member, Apps, Invite to a session: one of your upcoming sessions or events (planned by you or with
// you in), sent to them by DM with I'm in.
public sealed class EventInviteCommand(IDbContextFactory<BotDbContext> dbFactory, TimeZones zones, TimeProvider time) : GuildCommandModule
{
    [UserCommand("Invite to a session", Contexts = [InteractionContextType.Guild])]
    public async Task<InteractionCallbackProperties> InviteAsync(User user)
    {
        if (user.Id == Context.User.Id || user.IsBot)
            return InteractionCallback.Message(Replies.Ephemeral(user.IsBot ? "Bots don't come to sessions." : "That's you: right-click someone else."));
        await using var db = await dbFactory.CreateDbContextAsync();
        var now = time.GetUtcNow();
        var mine = db.EventRsvps.Where(r => r.UserId == Context.User.Id && r.Status == RsvpStatuses.In).Select(r => r.EventId);
        var upcoming = await db.Events.AsNoTracking()
            .Where(e => e.GuildId == GuildId && e.State == EventStates.Scheduled && e.StartsAt > now && (e.CreatorId == Context.User.Id || mine.Contains(e.Id)))
            .OrderBy(e => e.StartsAt).Take(25).ToListAsync();
        if (upcoming.Count == 0)
            return InteractionCallback.Message(Replies.Ephemeral("You have nothing coming up to invite them to: plan something, or sign up for something."));
        var (zone, _) = await zones.ForAsync(GuildId, Context.User.Id);
        var options = upcoming.Select(e => new StringMenuSelectOptionProperties(e.Title.Length <= 100 ? e.Title : e.Title[..100], e.Id.ToString())
        {
            Description = NodaTime.Instant.FromDateTimeOffset(e.StartsAt!.Value).InZone(zone).ToString("ddd d MMM HH:mm", null),
        });
        return InteractionCallback.Modal(new ModalProperties($"eventinvite:{user.Id}", "Invite to a session")
        {
            new LabelProperties("Which one?", new StringMenuProperties("event", options)),
        });
    }
}

public sealed class EventInviteModal(EventBoard board, RestClient rest, ILogger<EventInviteModal> logger) : GuildModalModule
{
    [ComponentInteraction("eventinvite")]
    public async Task InviteAsync(ulong userId)
    {
        var picked = Context.Components.OfType<Label>().Select(l => l.Component).OfType<StringMenu>().FirstOrDefault()?.SelectedValues is [var value, ..] && long.TryParse(value, out var id) ? id : 0;
        await RespondAsync(InteractionCallback.DeferredMessage(MessageFlags.Ephemeral));
        string result;
        if (await board.FindAsync(GuildId, picked) is not { State: EventStates.Scheduled, StartsAt: { } start } e)
            result = "That isn't coming up any more.";
        else
        {
            var at = start.ToUnixTimeSeconds();
            var link = e.MessageId is { } message ? $" · [see it]({Notifier.Link(e.GuildId, e.ChannelId, message)})" : "";
            try
            {
                var dm = await rest.GetDMChannelAsync(userId);
                await dm.SendMessageAsync(new()
                {
                    Content = $"**{Context.User.GlobalName ?? Context.User.Username}** invites you to **{e.Title}** in **{Guild.Name}**: <t:{at}:F> (<t:{at}:R>){link}",
                    Components = [new ActionRowProperties { new ButtonProperties($"eventinvitein:{GuildId}:{e.Id}", "I'm in", ButtonStyle.Success) }],
                });
                result = $"Invited <@{userId}> to **{e.Title}**.";
            }
            catch (RestException ex)
            {
                logger.LogDebug("Inviting {UserId} to event {EventId} failed: {Message}", userId, e.Id, ex.Message);
                result = $"<@{userId}> doesn't take DMs from the bot: tell them yourself, it's <t:{at}:F>.";
            }
        }
        await ModifyResponseAsync(m => (m.Content, m.AllowedMentions) = (result, AllowedMentionsProperties.None));
    }
}

// I'm in, pressed in the invitation's DM: signs them up, if they're still in the server.
public sealed class EventInviteButtons(EventBoard board, PanelAccess members) : ComponentInteractionModule<ButtonInteractionContext>
{
    [ComponentInteraction("eventinvitein")]
    public async Task InAsync(ulong guildId, long eventId)
    {
        await RespondAsync(InteractionCallback.DeferredModifyMessage);
        var said = await members.InAsync(guildId, Context.User.Id) is null ? "You're not in that server any more." : await board.RsvpAsync(eventId, Context.User.Id, RsvpStatuses.In);
        await ModifyResponseAsync(m => (m.Content, m.Components) = ($"{Context.Message.Content}\n{said}", []));
    }
}
