using NetCord.Gateway;
using NetCord.Hosting.Gateway;

namespace THOBOTTO.Sounds;

// Discord's soundboard changing (sounds added, renamed or removed there: events the Discord library passes through
// raw) and its sounds being played in a voice channel.
public sealed class SoundEvents(SoundBoard board) : IUnknownEventGatewayHandler, IVoiceChannelEffectSendGatewayHandler
{
    public ValueTask HandleAsync(UnknownEventEventArgs arg)
    {
        if (arg.Name.StartsWith("GUILD_SOUNDBOARD_SOUND", StringComparison.Ordinal) && arg.Data.TryGetProperty("guild_id", out var guild) && ulong.TryParse(guild.GetString(), out var guildId))
            board.DiscordChanged(guildId);
        return default;
    }

    public ValueTask HandleAsync(VoiceChannelEffectSendEventArgs arg)
        => arg.SoundId is { } soundId ? new(board.OnDiscordPlayAsync(arg.GuildId, arg.UserId, soundId)) : default;
}
