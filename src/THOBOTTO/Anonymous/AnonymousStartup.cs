using NetCord.Gateway;

using THOBOTTO.Modules;
using THOBOTTO.Reposts;

namespace THOBOTTO.Anonymous;

public static class AnonymousStartup
{
    public static IServiceCollection AddAnonymous(this IServiceCollection services)
    {
        services.AddHttpClient(nameof(AnonymousChannels), c => c.Timeout = TimeSpan.FromMinutes(1)).AddAsKeyed(ServiceLifetime.Singleton);
        return services
            .AddSingleton<AnonymousChannels>()
            .AddHostedService(s => s.GetRequiredService<AnonymousChannels>())
            .AddSingleton<IRewrite, AnonymousVoice>()
            .AddSingleton<IPostingSource, AnonymousPostings>();
    }
}

public sealed class AnonymousPostings(SettingsStore settings) : IPostingSource
{
    public async Task<IReadOnlyList<Posting>> PostingsAsync(Guild guild)
        => [.. (await settings.GetAsync<AnonymousRules>(guild.Id, AnonymousChannels.ModuleId)).ChannelIds
            .Select(c => new Posting(AnonymousChannels.ModuleId, "Posts under no one's name", new Landing.In(c), Posting.Settings(guild.Id, AnonymousChannels.ModuleId)))];
}
