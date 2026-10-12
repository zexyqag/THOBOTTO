using NetCord.Gateway;
using NetCord.Hosting.Gateway;

namespace THOBOTTO.Discord;

public sealed class VoiceStateHandler(VoicePresence presence) : IVoiceStateUpdateGatewayHandler
{
    public ValueTask HandleAsync(VoiceState arg) => new(presence.RecordAsync(arg));
}

public sealed class VoiceGuildCreateHandler(VoicePresence presence) : IGuildCreateGatewayHandler
{
    public ValueTask HandleAsync(GuildCreateEventArgs arg) => arg.Guild is { } guild ? new(presence.SeedAsync(guild)) : default;
}
