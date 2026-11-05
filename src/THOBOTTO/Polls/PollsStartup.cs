using THOBOTTO.Members;
using THOBOTTO.Modules;

namespace THOBOTTO.Polls;

public static class PollsStartup
{
    public static IServiceCollection AddPolls(this IServiceCollection services)
        => services
            .AddSingleton<PollBook>()
            .AddSingleton(PollBook.Happening)
            .AddHostedService(s => s.GetRequiredService<PollBook>())
            .AddSingleton<IMemberData, PollsData>()
            .AddSingleton<IPostingSource, PollPostings>();
}
