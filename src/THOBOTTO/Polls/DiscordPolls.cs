using NetCord;
using NetCord.Gateway;
using NetCord.Hosting.Gateway;
using NetCord.Rest;

using THOBOTTO.Happenings;
using THOBOTTO.Modules;

namespace THOBOTTO.Polls;

// Discord's own polls, made with the + beside the message box, taken in as the bot's are (as set: one way only, so off
// unless chosen): when one ends (Discord then posts its "poll ended" message), its result goes to the happenings forum.
// The other way isn't offered: the bot can't vote for anyone, so a copy of the bot's poll in Discord's would split the votes.
public sealed class DiscordPolls(ModuleState modules, SettingsStore settings, HappeningForum forum, RestClient rest, ILogger<DiscordPolls> logger) : IMessageCreateGatewayHandler
{
    public async ValueTask HandleAsync(Message message)
    {
        if (message.Type != MessageType.PollResult || message.GuildId is not { } guildId || message.MessageReference is not { } ended
            || !await modules.IsEnabledAsync(guildId, PollBook.ModuleId) || !(await settings.GetAsync<PollRules>(guildId, PollBook.ModuleId)).TakeInDiscordPolls)
            return;
        try
        {
            var asked = await rest.GetMessageAsync(ended.ChannelId, ended.MessageId);
            if (asked.Poll is not { } poll)
                return;
            var options = poll.Answers.Select(a => $"{(a.PollMedia.Emoji is { Id: null, Name: { } emoji } ? emoji + " " : "")}{a.PollMedia.Text}").ToList();
            var counts = poll.Answers.Select(a => poll.Results?.Answers.FirstOrDefault(c => c.AnswerId == a.AnswerId)?.Count ?? 0).ToList();
            var link = $"https://discord.com/channels/{guildId}/{ended.ChannelId}/{ended.MessageId}";
            var card = PollBook.Embed(poll.Question.Text ?? "", options, counts, closed: true, poll.ExpiresAt ?? message.CreatedAt,
                $"Asked by <@{asked.Author.Id}> in [Discord's poll]({link}){(poll.AllowMultiselect ? ", several answers each" : "")}");
            await forum.ShowAsync(guildId, PollBook.Happening.Id, (long)ended.MessageId, HappeningStates.Open, new(poll.Question.Text ?? "Poll", [card], []));
        }
        catch (RestException ex)
        {
            logger.LogWarning("Taking in Discord's poll {MessageId} in {ChannelId} of {GuildId} failed: {Message}", ended.MessageId, ended.ChannelId, guildId, ex.Message);
        }
    }
}
