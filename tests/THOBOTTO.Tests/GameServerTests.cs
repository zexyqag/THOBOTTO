using THOBOTTO.GameServers;

namespace THOBOTTO.Tests;

public class GameServerTests
{
    private static GameServer Server(string passwordFor, ulong? role = null)
        => new() { Game = "minecraft", Host = "play.example.com", Port = 25565, PasswordFor = passwordFor, PasswordRoleId = role };

    [Fact]
    public void The_password_goes_to_whoever_its_for()
    {
        Assert.True(ServerDetails.MaySee(Server(PasswordShown.Card), []));
        Assert.True(ServerDetails.MaySee(Server(PasswordShown.Anyone), []));
        Assert.False(ServerDetails.MaySee(Server(PasswordShown.Role, 7), [8]));
        Assert.True(ServerDetails.MaySee(Server(PasswordShown.Role, 7), [8, 7]));
    }

    [Fact]
    public void Players_join_where_they_are_told_to()
    {
        var server = Server(PasswordShown.Anyone);
        Assert.Equal("play.example.com:25565", server.JoinAt);
        server.JoinAddress = "mc.example.com";
        Assert.Equal("mc.example.com", server.JoinAt);
    }

    [Fact]
    public void Details_are_checked_before_they_are_kept()
    {
        Assert.Null(ServerDetails.Problem("mc.example.com:25565", "https://modrinth.com/modpack/x", PasswordShown.Anyone, null));
        Assert.NotNull(ServerDetails.Problem("not an address", null, PasswordShown.Anyone, null));
        Assert.NotNull(ServerDetails.Problem(null, "javascript:alert(1)", PasswordShown.Anyone, null));
        Assert.NotNull(ServerDetails.Problem(null, null, PasswordShown.Role, null));
        Assert.NotNull(ServerDetails.Problem(null, null, "everyone", null));
        Assert.Null(ServerDetails.Clean("   ", 10));
        Assert.Equal("All the Mo", ServerDetails.Clean(" All the Mods 9 ", 10));
    }
}
