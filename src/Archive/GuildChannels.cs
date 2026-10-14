using System.Net;

using NetCord;
using NetCord.Rest;

namespace THOBOTTO.Archive;

// A server's channels and threads as Discord lists them now. Listing a channel's archived threads can be
// refused (private ones need Manage Threads), so those channels are named: a thread of theirs that went
// unlisted isn't taken for deleted.
public sealed record GuildChannelList(IReadOnlyList<IGuildChannel> Channels, IReadOnlySet<ulong> UnlistedParents)
{
    public static bool IsThread(ChannelType type) => type is ChannelType.PublicGuildThread or ChannelType.PrivateGuildThread or ChannelType.AnnouncementGuildThread;

    // Whether the listing can say a known channel or thread is gone.
    public bool Vouches(ChannelType type, ulong? parentId) => !IsThread(type) || parentId is not { } parent || !UnlistedParents.Contains(parent);
}

public static class GuildChannels
{
    // Every channel, the active threads, and the archived public and private threads of the channels that have them.
    public static async Task<GuildChannelList> ListAsync(RestClient rest, ulong guildId, CancellationToken ct)
    {
        var found = new Dictionary<ulong, IGuildChannel>();
        var unlisted = new HashSet<ulong>();
        foreach (var channel in await rest.GetGuildChannelsAsync(guildId, cancellationToken: ct))
            found[channel.Id] = channel;
        foreach (var thread in await rest.GetActiveGuildThreadsAsync(guildId, cancellationToken: ct))
            found[thread.Id] = thread;

        // Voice channels have a text chat but no threads.
        foreach (var channel in found.Values.Where(c => c is (TextGuildChannel and not IVoiceGuildChannel and not GuildThread) or ForumGuildChannel or MediaForumGuildChannel).ToList())
        {
            if (!await AddArchivedThreadsAsync(rest, channel.Id, found, ct))
                unlisted.Add(channel.Id);
        }
        return new([.. found.Values], unlisted);
    }

    private static async Task<bool> AddArchivedThreadsAsync(RestClient rest, ulong channelId, Dictionary<ulong, IGuildChannel> found, CancellationToken ct)
    {
        try
        {
            await foreach (var thread in rest.GetPublicArchivedGuildThreadsAsync(channelId).WithCancellation(ct))
                found[thread.Id] = thread;
            await foreach (var thread in rest.GetPrivateArchivedGuildThreadsAsync(channelId).WithCancellation(ct))
                found[thread.Id] = thread;
            return true;
        }
        catch (RestException ex) when (ex.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.BadRequest)
        {
            // No access to this channel's (private) threads, or a channel type without threads.
            return false;
        }
    }
}
