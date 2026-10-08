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
        var discord = await access.FollowsDiscordAsync(GuildId);
        var lines = BotPermissions.All.Select(p =>
        {
            var who = grants.Where(g => g.Permission == p.Id).Select(g => $"<@&{g.RoleId}>").ToList();
            if (discord)
                who.Add($"Discord's {p.Discord}");
            return $"`{p.Id}`: {(who.Count == 0 ? "owner only" : string.Join(", ", who))}\n-# {p.Description}";
        });

        var mode = discord ? "Following Discord's permissions too (`/perms mode`)." : "Only the bot's own grants count (`/perms mode`).";
        return new()
        {
            Embeds = [new() { Title = "Bot permissions", Description = mode + "\n\n" + string.Join('\n', lines) }],
            Flags = MessageFlags.Ephemeral,
            AllowedMentions = AllowedMentionsProperties.None,
        };
    }

    [SubSlashCommand("mode", "Whether Discord's own permissions also count (server owner only)")]
    public async Task<InteractionMessageProperties> ModeAsync(
        [SlashCommandParameter(Description = "Bot: only grants from /perms. Discord: Discord's permissions count too")] PermissionMode mode)
    {
        if (Context.Guild!.OwnerId != Context.User.Id)
            return Replies.Ephemeral("Only the server owner can change this.");
        await access.SetFollowDiscordAsync(GuildId, mode == PermissionMode.Discord, Context.User.Id);
        return Replies.Ephemeral(mode == PermissionMode.Discord
            ? "Discord's permissions count now, next to the bot's grants: e.g. Kick Members allows `mod.kick`, Manage Server the settings. `/perms list` shows which allows what."
            : "Only the bot's own grants count now.");
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

public enum PermissionMode
{
    Bot,
    Discord,
}
