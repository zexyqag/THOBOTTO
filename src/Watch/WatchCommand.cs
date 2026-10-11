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
        var guild = Context.Guild!;
        var member = (GuildUser)Context.User;
        if (!presence.Snapshot(guild.Id).TryGetValue(member.Id, out var where))
            return InteractionCallback.Message(Replies.Ephemeral("Join a voice channel first."));
        _ = Task.Run(async () => await rooms.AnnounceAsync(where.ChannelId, $"<@{member.Id}>: {await rooms.AddAsync(guild, member, where.ChannelId, video)}"));
        return InteractionCallback.LaunchActivity;
    }
}
