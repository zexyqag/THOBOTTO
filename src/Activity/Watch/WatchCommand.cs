using NetCord;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;

using THOBOTTO.Discord;

namespace THOBOTTO.Activity.Watch;

public sealed class WatchCommand(WatchRooms rooms, VoicePresence presence) : GuildCommandModule
{
    // Opens the Activity for whoever asked, and adds the video meanwhile (what came of it goes to the channel);
    // or takes a queued video out instead.
    [SlashCommand("watch", "Watch a video together in your voice channel (a link, or search words)", Contexts = [InteractionContextType.Guild])]
    public async Task WatchAsync(
        [SlashCommandParameter(Description = "A YouTube (or other) link, or search words", MaxLength = 300)] string? video = null,
        [SlashCommandParameter(Description = "Instead, take a video out of the queue: its number", MinValue = 1, MaxValue = 100)] int? remove = null)
    {
        var member = (GuildUser)Context.User;
        if (!presence.Snapshot(GuildId).TryGetValue(member.Id, out var where))
        {
            await RespondAsync(InteractionCallback.Message(Replies.Ephemeral("Join a voice channel first.")));
            return;
        }
        if (remove is { } number)
        {
            await RespondAsync(InteractionCallback.Message(Replies.Ephemeral(await rooms.RemoveAsync(Guild, member, where.ChannelId, number))));
            return;
        }
        if (video is { Length: > 0 })
            _ = Task.Run(async () => await rooms.AnnounceAsync(GuildId, where.ChannelId, $"<@{member.Id}>: {await rooms.AddAsync(Guild, member, where.ChannelId, video)}"));
        await RespondAsync(InteractionCallback.LaunchActivity);
    }
}
