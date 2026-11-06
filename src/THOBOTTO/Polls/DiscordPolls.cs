using System.Collections.Concurrent;

using NetCord;
using NetCord.Gateway;
using NetCord.Hosting.Gateway;
using NetCord.Rest;

using THOBOTTO.Happenings;
using THOBOTTO.Modules;

namespace THOBOTTO.Polls;

// Discord's own polls, made with the + beside the message box, taken in as the bot's are (as set: one way only, so off
// unless chosen): when one ends, its result goes to the happenings forum. Its message is updated as final at once;
// Discord's "poll ended" message can follow tens of seconds later, so either takes it in, once.
// The other way isn't offered: the bot can't vote for anyone, so a copy of the bot's poll in Discord's would split the votes.
public sealed class DiscordPolls(ModuleState modules, SettingsStore settings, HappeningForum forum, RestClient rest, ILogger<DiscordPolls> logger)
    : IMessageUpdateGatewayHandler, IMessageCreateGatewayHandler
{
    private readonly ConcurrentDictionary<ulong, bool> _taken = new();

    ValueTask IMessageUpdateGatewayHandler.HandleAsync(Message message)
        => message.Poll?.Results is { IsFinalized: true } && message.GuildId is { } guildId ? new(TakeInAsync(guildId, message.ChannelId, message.Id)) : default;

    ValueTask IMessageCreateGatewayHandler.HandleAsync(Message message)
        => message.Type == MessageType.PollResult && message.GuildId is { } guildId && message.MessageReference is { } ended
            ? new(TakeInAsync(guildId, ended.ChannelId, ended.MessageId))
            : default;

    private async Task TakeInAsync(ulong guildId, ulong channelId, ulong messageId)
    {
        if (!await modules.IsEnabledAsync(guildId, PollBook.ModuleId) || !(await settings.GetAsync<PollRules>(guildId, PollBook.ModuleId)).TakeInDiscordPolls
            || !_taken.TryAdd(messageId, true))
            return;
        try
        {
            var asked = await rest.GetMessageAsync(channelId, messageId);
            if (asked.Poll is not { } poll)
                return;
            var options = poll.Answers.Select(a => $"{(a.PollMedia.Emoji is { Id: null, Name: { } emoji } ? emoji + " " : "")}{a.PollMedia.Text}").ToList();
            var counts = poll.Answers.Select(a => poll.Results?.Answers.FirstOrDefault(c => c.AnswerId == a.AnswerId)?.Count ?? 0).ToList();
            var link = $"https://discord.com/channels/{guildId}/{channelId}/{messageId}";
            var card = PollBook.Embed(poll.Question.Text ?? "", options, counts, closed: true, poll.ExpiresAt ?? asked.CreatedAt,
                $"Asked by <@{asked.Author.Id}> in [Discord's poll]({link}){(poll.AllowMultiselect ? ", several answers each" : "")}");
            await forum.ShowAsync(guildId, PollBook.Happening.Id, (long)messageId, HappeningStates.Open, new(poll.Question.Text ?? "Poll", [card], []));
        }
        catch (RestException ex)
        {
            _taken.TryRemove(messageId, out _);
            logger.LogWarning("Taking in Discord's poll {MessageId} in {ChannelId} of {GuildId} failed: {Message}", messageId, channelId, guildId, ex.Message);
        }
    }
}
