using NetCord;
using NetCord.Rest;
using NetCord.Services;
using NetCord.Services.ApplicationCommands;

namespace THOBOTTO.Modules;

[SlashCommand("modules", "Turn bot modules on or off",
    DefaultGuildPermissions = Permissions.Administrator,
    Contexts = [InteractionContextType.Guild])]
[RequireUserPermissions<ApplicationCommandContext>(Permissions.Administrator)]
public sealed class ModuleCommands(ModuleState state) : ApplicationCommandModule<ApplicationCommandContext>
{
    private ulong GuildId => Context.Interaction.GuildId!.Value;

    [SubSlashCommand("list", "Show every module and whether it's on")]
    public async Task<InteractionMessageProperties> ListAsync()
    {
        if (ModuleRegistry.All.Count == 0)
            return Replies.Ephemeral("No modules exist yet.");

        var lines = new List<string>();
        foreach (var module in ModuleRegistry.All)
        {
            var mark = await state.IsEnabledAsync(GuildId, module.Id) ? "on" : "off";
            lines.Add($"`{module.Id}` ({mark}): {module.Description}");
        }

        return Replies.Ephemeral(string.Join('\n', lines));
    }

    [SubSlashCommand("enable", "Turn a module on")]
    public Task<InteractionMessageProperties> EnableAsync(
        [SlashCommandParameter(Description = "Module", AutocompleteProviderType = typeof(ModuleAutocomplete))] string module)
        => SetAsync(module, true);

    [SubSlashCommand("disable", "Turn a module off")]
    public Task<InteractionMessageProperties> DisableAsync(
        [SlashCommandParameter(Description = "Module", AutocompleteProviderType = typeof(ModuleAutocomplete))] string module)
        => SetAsync(module, false);

    private async Task<InteractionMessageProperties> SetAsync(string id, bool enabled)
    {
        if (ModuleRegistry.Find(id) is null)
            return Replies.Ephemeral($"There is no module called `{id}`.");

        await state.SetEnabledAsync(GuildId, id, enabled, Context.User.Id);
        return Replies.Ephemeral($"`{id}` is now {(enabled ? "on" : "off")}.");
    }
}
