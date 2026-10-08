using NetCord;
using NetCord.Services;
using NetCord.Services.ApplicationCommands;

namespace THOBOTTO.Access;

public sealed class RequirePermissionAttribute(string permission) : PreconditionAttribute<ApplicationCommandContext>
{
    public override async ValueTask<PreconditionResult> EnsureCanExecuteAsync(ApplicationCommandContext context, IServiceProvider? serviceProvider)
    {
        if (context.Guild is not { } guild || context.User is not GuildUser user)
            return PreconditionResult.Fail("This only works in a server.");

        var access = serviceProvider!.GetRequiredService<AccessControl>();
        return await access.CanAsync(guild, user, permission)
            ? PreconditionResult.Success
            : PreconditionResult.Fail($"You need the `{permission}` permission for that. Ask someone with `{BotPermissions.ManagePermissions}`.");
    }
}
