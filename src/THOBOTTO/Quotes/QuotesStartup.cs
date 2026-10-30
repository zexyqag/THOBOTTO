using THOBOTTO.Archive;
using THOBOTTO.Helpers;
using THOBOTTO.Members;
using THOBOTTO.Modules;
using THOBOTTO.Overview;
using THOBOTTO.Panel;
using THOBOTTO.Voice.Commands;
using THOBOTTO.Wrapped;

namespace THOBOTTO.Quotes;

public static class QuotesStartup
{
    public static IServiceCollection AddQuotes(this IServiceCollection services)
        => services
            .AddSingleton<QuoteBook>()
            .AddSingleton<IWidgetKind, LatestQuotesWidget>()
            .AddSingleton<VoiceQuotes>()
            .AddSingleton<IHelperAware>(s => s.GetRequiredService<VoiceQuotes>())
            .AddSingleton<IVoiceActions>(s => s.GetRequiredService<VoiceQuotes>())
            .AddSingleton<IExportSource, QuotesExport>()
            .AddSingleton<ISettingsSummary, QuotesSummary>()
            .AddSingleton<ISettingsSummary, QuotesInShowcaseSummary>()
            .AddSingleton<IWrappedSection, QuotesWrapped>()
            .AddSingleton<IMemberData, QuotesMemberData>()
            .AddSingleton(new GuildNavEntry("quotes", "Quotes", GuildNavGroup.Community, 70, Module: QuoteCommands.ModuleId));
}
