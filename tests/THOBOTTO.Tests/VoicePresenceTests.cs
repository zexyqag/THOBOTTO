using Microsoft.Extensions.Logging.Abstractions;

using NetCord.Gateway;
using NetCord.Gateway.JsonModels;
using NetCord.JsonModels;

using THOBOTTO.Discord;

namespace THOBOTTO.Tests;

public class VoicePresenceTests
{
    private const ulong GuildId = 1;

    private static VoiceState State(ulong userId, ulong? channelId)
        => new(new JsonVoiceState { UserId = userId, ChannelId = channelId }, GuildId, null!);

    [Fact]
    public async Task Subscribers_hear_each_change_in_order_once_it_is_recorded()
    {
        var presence = new VoicePresence(NullLogger<VoicePresence>.Instance);
        var heard = new List<(string Who, ulong? Before, ulong? Now, ulong? Recorded)>();
        presence.Changed += (state, before) =>
        {
            heard.Add(("first", before, state.ChannelId, presence.Snapshot(GuildId).GetValueOrDefault(state.UserId)?.ChannelId));
            return Task.CompletedTask;
        };
        presence.Changed += (state, before) =>
        {
            heard.Add(("second", before, state.ChannelId, null));
            return Task.CompletedTask;
        };

        await presence.RecordAsync(State(7, 10));
        await presence.RecordAsync(State(7, 11));
        await presence.RecordAsync(State(7, null));

        Assert.Equal(
        [
            ("first", null, 10UL, 10UL), ("second", null, 10UL, null),
            ("first", 10UL, 11UL, 11UL), ("second", 10UL, 11UL, null),
            ("first", 11UL, null, null), ("second", 11UL, null, null),
        ], heard);
    }

    [Fact]
    public async Task A_failing_subscriber_stops_neither_the_next_nor_the_caller()
    {
        var presence = new VoicePresence(NullLogger<VoicePresence>.Instance);
        var heard = 0;
        presence.Changed += (_, _) => throw new InvalidOperationException("a feature broke");
        presence.Changed += (_, _) => Task.FromException(new InvalidOperationException("another did too"));
        presence.Changed += (_, _) =>
        {
            heard++;
            return Task.CompletedTask;
        };

        await presence.RecordAsync(State(7, 10));

        Assert.Equal(1, heard);
        Assert.Equal(10UL, presence.Snapshot(GuildId)[7].ChannelId);
    }

    [Fact]
    public async Task Bots_already_in_voice_when_the_guild_arrives_are_known_as_bots()
    {
        var presence = new VoicePresence(NullLogger<VoicePresence>.Instance);
        var guild = new Guild(new JsonGuild
        {
            Id = GuildId,
            VoiceStates = [new() { UserId = 7, ChannelId = 10 }, new() { UserId = 8, ChannelId = 10 }],
            Users = [new() { User = new() { Id = 7, Username = "helper", IsBot = true } }, new() { User = new() { Id = 8, Username = "member" } }],
        }, 99, (NetCord.Rest.RestClient)null!, IDictionaryProvider.OfDictionary);

        await presence.SeedAsync(guild);

        Assert.Equal([(7UL, true), (8UL, false)], presence.Snapshot(GuildId).OrderBy(p => p.Key).Select(p => (p.Key, p.Value.IsBot)));
    }
}
