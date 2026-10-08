using NetCord.Gateway;
using NetCord.Hosting.Gateway;

namespace THOBOTTO.Access;

public sealed class RoleDeleteHandler(AccessControl access) : IRoleDeleteGatewayHandler
{
    public async ValueTask HandleAsync(RoleDeleteEventArgs arg) => await access.RemoveRoleAsync(arg.GuildId, arg.RoleId);
}
