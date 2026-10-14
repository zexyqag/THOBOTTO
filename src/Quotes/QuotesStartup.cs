using THOBOTTO.Archive;
using THOBOTTO.Helpers;
using THOBOTTO.Voice.Commands;

namespace THOBOTTO.Quotes;

public static class QuotesStartup
{
    public static IServiceCollection AddQuotes(this IServiceCollection services)
        => services
            .AddSingleton<QuoteBook>()
            .AddSingleton<VoiceQuotes>()
            .AddSingleton<IHelperAware>(s => s.GetRequiredService<VoiceQuotes>())
            .AddSingleton<IVoiceActions>(s => s.GetRequiredService<VoiceQuotes>())
            .AddSingleton<IExportSource, QuotesExport>();
}
