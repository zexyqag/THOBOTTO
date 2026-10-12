using NetCord;
using NetCord.Rest;

namespace THOBOTTO.Discord;

public static class DiscordStartup
{
    // What the bot's application allows, asked of Discord with its token.
    public static async Task<DiscordApp> LookupAsync(string token)
    {
        using var rest = new RestClient(new BotToken(token));
        return new((await rest.GetCurrentApplicationAsync()).Flags ?? default);
    }

    public static IServiceCollection AddDiscord(this IServiceCollection services, DiscordApp app)
        => services
            .AddSingleton(app)
            .AddSingleton<VoicePresence>()
            .AddSingleton<DiscordApi>()
            .AddSingleton<BotOwners>();
}
