using NetCord;

using THOBOTTO.Discord;
using THOBOTTO.Gate;
using THOBOTTO.Modules;
using THOBOTTO.Music;

namespace THOBOTTO.Tests;

public class ModuleNeedsTests
{
    private static ModuleState With(ApplicationFlags flags) => new(null!, TimeProvider.System, new DiscordApp(flags));

    [Fact]
    public void The_gate_needs_the_members_intent()
    {
        Assert.Contains("Server Members intent", With(default).Needs(Gatekeeper.ModuleId));
        Assert.Null(With(ApplicationFlags.GatewayGuildUsers).Needs(Gatekeeper.ModuleId));
        Assert.Null(With(ApplicationFlags.GatewayGuildUsersLimited).Needs(Gatekeeper.ModuleId));
    }

    [Fact]
    public void Other_modules_need_nothing()
    {
        Assert.Null(With(default).Needs(MusicService.ModuleId));
        Assert.Null(With(default).Needs("no-such-module"));
    }
}
