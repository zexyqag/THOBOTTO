using NetCord.Gateway;
using NetCord.Hosting.Gateway;

namespace THOBOTTO.Live;

public sealed class LivePresenceHandler(LiveWatch watch) : IPresenceUpdateGatewayHandler
{
    public ValueTask HandleAsync(Presence arg) => new(watch.SeenAsync(arg.GuildId, arg.User.Id, arg.Activities));
}

// What changed while the bot wasn't connected: streams begun or ended meanwhile.
public sealed class LiveGuildCreateHandler(LiveWatch watch) : IGuildCreateGatewayHandler
{
    public ValueTask HandleAsync(GuildCreateEventArgs arg) => arg.Guild is { } guild ? new(watch.CatchUpAsync(guild)) : default;
}
