namespace THOBOTTO.Translation;

public static class TranslationStartup
{
    public static IServiceCollection AddTranslation(this IServiceCollection services)
    {
        services.AddHttpClient(nameof(Translator), c => c.Timeout = TimeSpan.FromSeconds(30)).AddAsKeyed(ServiceLifetime.Singleton);
        return services.AddSingleton<Translator>();
    }
}
