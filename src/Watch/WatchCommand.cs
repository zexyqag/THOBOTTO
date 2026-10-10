using NetCord;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;

using THOBOTTO.Voice;

namespace THOBOTTO.Watch;

public sealed class WatchCommand(WatchRooms rooms, VoicePresence presence) : ApplicationCommandModule<ApplicationCommandContext>
{
    // Opens the Activity for whoever asked, and adds the video meanwhile (what came of it goes to the channel).
    [SlashCommand("watch", "Watch a video together in your voice channel (a link, or search words)", Contexts = [InteractionContextType.Guild])]
    public InteractionCallbackProperties Watch(
        [SlashCommandParameter(Description = "A YouTube (or other) link, or search words", MaxLength = 300)] string video)
    {
        var guildId = Context.Guild!.Id;
        if (!presence.Snapshot(guildId).TryGetValue(Context.User.Id, out var where))
            return InteractionCallback.Message(Replies.Ephemeral("Join a voice channel first."));
        var userId = Context.User.Id;
        _ = Task.Run(async () => await rooms.AnnounceAsync(where.ChannelId, $"<@{userId}>: {await rooms.AddAsync(guildId, where.ChannelId, userId, video)}"));
        return InteractionCallback.LaunchActivity;
    }
}
