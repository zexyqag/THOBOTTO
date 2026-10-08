using NetCord;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;

namespace THOBOTTO.Access;

[SlashCommand("perms", "Bot permissions", Contexts = [InteractionContextType.Guild])]
public sealed class PermissionCommands(AccessControl access) : ApplicationCommandModule<ApplicationCommandContext>
{
    private ulong GuildId => Context.Interaction.GuildId!.Value;

    [SubSlashCommand("grant", "Give a role a bot permission")]
    [RequirePermission(BotPermissions.ManagePermissions)]
    public Task<InteractionMessageProperties> GrantAsync(
        [SlashCommandParameter(Description = "Role")] Role role,
        [SlashCommandParameter(Description = "Permission", AutocompleteProviderType = typeof(PermissionAutocomplete))] string permission)
        => SetAsync(role, permission, true);

    [SubSlashCommand("revoke", "Take a bot permission away from a role")]
    [RequirePermission(BotPermissions.ManagePermissions)]
    public Task<InteractionMessageProperties> RevokeAsync(
        [SlashCommandParameter(Description = "Role")] Role role,
        [SlashCommandParameter(Description = "Permission", AutocompleteProviderType = typeof(PermissionAutocomplete))] string permission)
        => SetAsync(role, permission, false);

    [SubSlashCommand("list", "Show which roles have which bot permissions")]
    public async Task<InteractionMessageProperties> ListAsync()
    {
        var grants = await access.ListAsync(GuildId);
        var lines = BotPermissions.All.Select(p =>
        {
            var roles = grants.Where(g => g.Permission == p.Id).Select(g => $"<@&{g.RoleId}>").ToList();
            return $"`{p.Id}`: {(roles.Count == 0 ? "owner only" : string.Join(", ", roles))}\n-# {p.Description}";
        });

        return Replies.Ephemeral(string.Join('\n', lines));
    }

    [SubSlashCommand("check", "Show what someone can do, and why")]
    public async Task<InteractionMessageProperties> CheckAsync(
        [SlashCommandParameter(Description = "Member (you if left out)")] GuildUser? user = null)
    {
        user ??= (GuildUser)Context.User;
        var permissions = await access.ExplainAsync(Context.Guild!, user);

        return Replies.Ephemeral(permissions.Count == 0
            ? $"<@{user.Id}> has no bot permissions."
            : $"<@{user.Id}> can:\n" + string.Join('\n', permissions.Select(p => $"`{p.Permission}` via {p.Source}")));
    }

    private async Task<InteractionMessageProperties> SetAsync(Role role, string permission, bool granted)
    {
        if (BotPermissions.Find(permission) is null)
            return Replies.Ephemeral($"There is no permission called `{permission}`. Pick one from the list.");

        var changed = await access.SetAsync(GuildId, role.Id, permission, granted, Context.User.Id);
        return Replies.Ephemeral((changed, granted) switch
        {
            (true, true) => $"<@&{role.Id}> now has `{permission}`.",
            (true, false) => $"<@&{role.Id}> no longer has `{permission}`.",
            (false, true) => $"<@&{role.Id}> already has `{permission}`.",
            (false, false) => $"<@&{role.Id}> didn't have `{permission}`.",
        });
    }
}
