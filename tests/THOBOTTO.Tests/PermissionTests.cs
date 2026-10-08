using THOBOTTO.Access;

namespace THOBOTTO.Tests;

public class PermissionTests
{
    [Fact]
    public void A_group_is_every_permission_with_its_prefix()
    {
        var mod = BotPermissions.Matching("mod.*").Select(p => p.Id).ToList();
        Assert.Contains(BotPermissions.ModBan, mod);
        Assert.Contains(BotPermissions.ModManage, mod);
        Assert.All(mod, id => Assert.StartsWith("mod.", id));
        Assert.DoesNotContain(BotPermissions.ModerateNicknames, mod);
    }

    [Fact]
    public void One_permission_or_nothing()
    {
        Assert.Equal([BotPermissions.ModKick], BotPermissions.Matching("mod.kick").Select(p => p.Id));
        Assert.Empty(BotPermissions.Matching("mod.fly"));
        Assert.Empty(BotPermissions.Matching("nothing.*"));
    }

    [Fact]
    public void Groups_are_offered_for_shared_prefixes()
    {
        var groups = BotPermissions.Groups.Select(g => g.Pattern).ToList();
        Assert.Contains("mod.*", groups);
        Assert.DoesNotContain("perms.*", groups);
    }
}
